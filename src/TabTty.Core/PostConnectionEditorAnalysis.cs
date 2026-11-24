namespace TabTty.Core;

internal sealed record PostConnectionCompletion(string Label, string InsertText);
internal sealed record PostConnectionSignature(string Name, string[] Parameters, int ActiveParameter);
internal sealed record PostConnectionEditorAnalysis(
    int ReplacementStart, int ReplacementLength, IReadOnlyList<PostConnectionCompletion> Completions,
    PostConnectionSignature? Signature, MacroSourceException? Diagnostic, int DiagnosticStart, int DiagnosticLength)
{
    internal static PostConnectionEditorAnalysis Analyze(string source, int caret)
    {
        caret = Math.Clamp(caret, 0, source.Length);
        var cursor = new PostConnectionCursor(caret);
        var editor = new MacroEditorSymbols(caret) { PostConnection = cursor };
        List<MacroEditorSymbol> symbols = [];
        MacroSourceException? error;
        try
        {
            symbols = MacroProgram.AnalyzePostConnection(source, editor, caret);
            error = editor.Error;
        }
        catch (MacroSourceException failure) { error = failure; }
        catch (ResourceLimitException) { error = new("MemoryLimit", new(1, 1)); }
        return cursor.Result(source, editor, symbols, error);
    }
}

internal enum PostConnectionSite { None, Statement, Expression, Condition, Member, Else, AfterElse }

// Captures expectations at the caret while the shared parser reads the actual statement,
// including its incomplete calls. The checker supplies inferred local and receiver types.
internal sealed class PostConnectionCursor(int caret)
{
    internal List<MacroToken> Tokens { private get; set; } = [];
    private PostConnectionSite site;
    private MacroType? expected;
    private MacroExpression? operand;
    private bool allowElse, insideString;

    internal void String(int start, int end, bool closed)
    {
        if (caret > start && (caret < end || !closed && caret == end)) insideString = true;
    }

    internal void Offer(PostConnectionSite candidate, MacroToken? previous, MacroToken current,
        MacroType? type, MacroExpression? expression)
    {
        if (caret < (previous?.End ?? 0) || caret > (current.Kind == "id" ? current.End : current.Offset) ||
            previous is { Kind: "id" } && previous.End == caret) return;
        if (candidate == PostConnectionSite.Else)
        {
            allowElse = previous?.Text == "}";
            return;
        }
        site = candidate;
        expected = type;
        operand = expression;
    }

    internal PostConnectionEditorAnalysis Result(string source, MacroEditorSymbols editor,
        List<MacroEditorSymbol> symbols, MacroSourceException? error)
    {
        var identifier = Tokens.Find(t => t.Kind == "id" && t.Offset <= caret && caret <= t.End);
        int start = identifier?.Offset ?? caret;
        int length = identifier?.Text.Length ?? 0;
        string prefix = source[start..caret];
        var completions = new List<PostConnectionCompletion>();
        var call = editor.ActiveCall;
        var signature = call is null || editor.InsideComment ? null : Signature(call);
        if (!editor.InsideComment && !insideString)
        {
            if (call is { CompleteOption: true } && MacroApi.NamedSignature(call.Name)?.NamedParameters is { } options &&
                PostConnectionMacro.Calls.ContainsKey(call.Name))
            {
                foreach (var option in options.Where(p => p.Index > 0 && !call.UsedSlots.Contains(p.Index)))
                    Add(option.Name, call.HasOptionColon ? option.Name : option.Name + ": ");
            }
            else if (call is null || signature is not null)
            {
                var type = expected ?? TypeOf(operand, symbols);
                switch (site)
                {
                    case PostConnectionSite.Statement:
                        foreach (string name in PostConnectionMacro.Calls.Keys) Add(name);
                        foreach (string name in PostConnectionMacro.LocalTypes) Add(name);
                        Add("var"); Add("if"); Add("return");
                        if (allowElse) Add("else");
                        break;
                    case PostConnectionSite.AfterElse: Add("if"); break;
                    case PostConnectionSite.Member:
                        if (type?.Name == "WaitMatch") Add("AlternativeName");
                        break;
                    case PostConnectionSite.Expression:
                    case PostConnectionSite.Condition:
                        if (type == MacroApi.Alternatives) { Add("["); break; }
                        foreach (string name in PostConnectionMacro.Calls.Keys)
                        {
                            var result = MacroApi.Signatures[name][0].Result;
                            if (result != MacroType.Void && CanStartValue(result.Name, type)) Add(name);
                        }
                        foreach (var local in symbols)
                            if (PostConnectionMacro.LocalTypes.Contains(local.Type) &&
                                CanStartValue(local.Type, type) && !MacroChecker.IsReserved(local.Name))
                                Add(local.Name);
                        if (type is null || type == MacroType.Bool) { Add("true"); Add("false"); }
                        break;
                }
            }
        }
        completions.Sort((left, right) => string.CompareOrdinal(left.Label, right.Label));
        var (errorStart, errorEnd) = DiagnosticRange(source, error);
        return new(start, length, completions, signature, error, errorStart, errorEnd - errorStart);

        void Add(string label, string? insertion = null)
        {
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                completions.Add(new(label, insertion ?? label));
        }
    }

    private bool CanStartValue(string candidate, MacroType? expectedType)
    {
        if (expectedType is null || candidate == expectedType.Name) return true;
        // A wait result can start a string expression via its AlternativeName member.
        if (expectedType == MacroType.String && candidate == "WaitMatch") return true;
        // In a predicate, equality can turn ordinary values into a boolean.
        return site == PostConnectionSite.Condition && candidate is "string" or "long" or "WaitMatch";
    }

    private static MacroType? TypeOf(MacroExpression? expression, List<MacroEditorSymbol> symbols)
    {
        if (expression is null) return null;
        if (expression.Type != MacroType.Unknown) return expression.Type;
        return expression switch
        {
            MacroCall call => PostConnectionMacro.Signature(call.Name)?.Result,
            MacroName name when symbols.FindLast(s => s.Name == name.Name) is { } local => new(local.Type),
            MacroLiteral literal => MacroChecker.ScalarType(literal.Value),
            MacroMember { Name: "AlternativeName" } => MacroType.String,
            _ => null,
        };
    }

    private static PostConnectionSignature? Signature(MacroEditorCall call)
    {
        if (!PostConnectionMacro.Calls.TryGetValue(call.Name, out int required)) return null;
        var signature = MacroApi.Signatures[call.Name][^1];
        var types = signature.Parameters;
        int index = call.NamedSlot ?? call.Parameter;
        if (index >= types.Length) return null;
        string[] parameters = new string[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            string name = signature.ParameterNames[i];
            string parameter = types[i] == MacroApi.Alternatives ? "[name: pattern, ...]" :
                MacroApi.NamedSignature(call.Name) is not null && i > 0 ? name + ": " + types[i] : types[i] + " " + name;
            parameters[i] = i < required ? parameter : "[" + parameter + "]";
        }
        return new(call.Name, parameters, index);
    }

    private MacroSourceRange DiagnosticRange(string source, MacroSourceException? error)
    {
        if (error is null) return default;
        int line = 1, start = 0;
        while (line < error.Position.Line && start < source.Length)
        {
            char c = source[start++];
            if (c == '\r' && start < source.Length && source[start] == '\n') start++;
            if (c is '\r' or '\n') line++;
        }
        start = Math.Min(source.Length, start + error.Position.Column - 1);
        var token = Tokens.Find(t => t.Offset == start);
        int end = Math.Min(source.Length, Math.Max(token?.End ?? start, start + error.Length));
        return new(start, end);
    }
}
