namespace TabTty.Core;

internal sealed record MacroBuiltinParameter(string Name, MacroType Type, int Index);
internal sealed record MacroSignature(MacroType Result, params MacroType[] Parameters)
{
    internal string[] ParameterNames { get; init; } = [];
    internal MacroBuiltinParameter[]? NamedParameters { get; init; }
}

internal static class MacroApi
{
    internal static readonly MacroBuiltinParameter LogDirectory = new("logDirectory", MacroType.String, 1);
    internal static readonly MacroBuiltinParameter LogFilenameTemplate = new("logFilenameTemplate", MacroType.String, 2);
    internal static readonly MacroBuiltinParameter LogCollision = new("logCollision", MacroType.String, 3);
    internal static readonly MacroBuiltinParameter MarkReceiveBoundary = new("markReceiveBoundary", MacroType.Bool, 1);
    internal static readonly MacroType Alternatives = new("NamedWaitAlternatives");
    internal static readonly MacroType WaitPattern = new("WaitPattern");
    internal static readonly Dictionary<string, MacroSignature[]> Signatures = Build();

    internal static MacroSignature? NamedSignature(string name) =>
        Signatures.TryGetValue(name, out var signatures)
            ? Array.Find(signatures, signature => signature.NamedParameters is not null)
            : null;

    private static Dictionary<string, MacroSignature[]> Build()
    {
        var v = MacroType.Void; var s = MacroType.String; var n = MacroType.Long;
        var d = MacroType.Double; var b = MacroType.Bool; var t = new MacroType("T");
        var list = MacroType.List(t); var strings = MacroType.List(s);
        var pattern = WaitPattern; var match = new MacroType("WaitMatch");
        var instant = new MacroType("Instant");
        var result = new Dictionary<string, MacroSignature[]>(StringComparer.Ordinal);
        void Add(string name, MacroSignature first, MacroSignature? second = null, string[]? names = null)
        {
            MacroSignature Describe(MacroSignature signature) =>
                signature with { ParameterNames = (names ?? []).Take(signature.Parameters.Length).ToArray() };
            result.Add(name, second is null ? [Describe(first)] : [Describe(first), Describe(second)]);
        }
        void AddWithOptions(string name, MacroType returns, params MacroBuiltinParameter[] parameters)
        {
            Array.Sort(parameters, (left, right) => left.Index.CompareTo(right.Index));
            Add(name, new(returns, parameters[0].Type),
                new(returns, parameters.Select(p => p.Type).ToArray()) { NamedParameters = parameters },
                parameters.Select(p => p.Name).ToArray());
        }
        AddWithOptions("Connect", v, new("profilePath", s, 0), LogDirectory, LogFilenameTemplate, LogCollision);
        Add("SetBaseDirectory", new(v, s), names: ["path"]);
        Add("ResetBaseDirectory", new(v)); Add("BaseDirectory", new(s));
        foreach (var name in new[] { "GetParentDirectory", "GetFileBaseName", "GetFileExtension" })
            Add(name, new(s, s), names: ["path"]);
        Add("Write", new(v, s), names: ["text"]);
        AddWithOptions("WriteLine", v, new("text", s, 0), MarkReceiveBoundary);
        foreach (var name in new[] { "WritePassword", "WritePasswordLine" })
            Add(name, new(v), new(v, s), ["name"]);
        Add("MarkReceiveBoundary", new(v));
        Add("Literal", new(pattern, s), new(pattern, s, b), ["text", "ignoreCase"]);
        Add("Regex", new(pattern, s), new(pattern, s, b), ["pattern", "ignoreCase"]);
        foreach (string name in new[] { "WaitLine", "WaitPrompt" })
            Add(name, new(match, pattern), new(match, pattern, n), ["pattern", "timeoutMs"]);
        foreach (string name in new[] { "WaitAnyLine", "WaitAnyPrompt" })
            Add(name, new(match, Alternatives), new(match, Alternatives, n), ["alternatives", "timeoutMs"]);
        Add("Sleep", new(v, n), names: ["milliseconds"]); Add("MinimizeWindow", new(v)); Add("RestoreWindow", new(v));
        Add("Dialog", new(new("DialogResult"), s, s, new("DialogButtons")),
            new(new("DialogResult"), s, s, new("DialogButtons"), b, s), ["title", "message", "buttons", "inputEnabled", "initialText"]);
        Add("Length", new(n, s), names: ["text"]); Add("Count", new(n, list), names: ["items"]);
        Add("Substring", new(s, s, n, n), names: ["text", "start", "length"]);
        foreach (string name in new[] { "Contains", "StartsWith", "EndsWith" })
            Add(name, new(b, s, s), new(b, s, s, b), ["text", "value", "ignoreCase"]);
        Add("IndexOf", new(n, s, s), new(n, s, s, b), ["text", "value", "ignoreCase"]);
        Add("Replace", new(s, s, s, s), names: ["text", "oldValue", "newValue"]);
        Add("Split", new(strings, s, s), names: ["text", "separator"]);
        Add("Join", new(s, s, strings), names: ["separator", "items"]);
        foreach (string name in new[] { "Trim", "ToUpperInvariant", "ToLowerInvariant" })
            Add(name, new(s, s), names: ["text"]);
        Add("Add", new(v, list, t), names: ["items", "value"]);
        Add("Insert", new(v, list, n, t), names: ["items", "index", "value"]);
        Add("RemoveAt", new(v, list, n), names: ["items", "index"]);
        Add("Clear", new(v, list), names: ["items"]); Add("Clone", new(list, list), names: ["items"]);
        Add("ParseLong", new(n, s), names: ["text"]); Add("ParseDouble", new(d, s), names: ["text"]);
        Add("FormatNumber", new(s, n, s), new(s, d, s), ["value", "format"]);
        Add("Now", new(instant)); Add("UtcNow", new(instant));
        Add("FormatTime", new(s, instant), new(s, instant, s), ["value", "format"]);
        Add("UnixMilliseconds", new(n, instant), names: ["value"]); Add("ElapsedMilliseconds", new(n));
        return result;
    }

    internal static MacroType? Member(MacroType target, string name) => (target.Name, name) switch
    {
        ("Macro", "Args") => MacroType.List(MacroType.String),
        ("Macro", "FullPath" or "FileName" or "BaseName" or "RunId") => MacroType.String,
        ("WaitMatch", "Index" or "Length") => MacroType.Long,
        ("WaitMatch", "Text" or "AlternativeName") => MacroType.String,
        ("WaitMatch", "Groups") => MacroType.List(new("CaptureGroup")),
        ("CaptureGroup", "Number" or "Index" or "Length") => MacroType.Long,
        ("CaptureGroup", "Name" or "Value") => MacroType.String,
        ("CaptureGroup", "Success") => MacroType.Bool,
        ("CaptureGroup", "Captures") => MacroType.List(MacroType.String),
        ("DialogResult", "Button") => new("DialogButton"),
        ("DialogResult", "HasText") => MacroType.Bool,
        ("DialogResult", "Text") => MacroType.String,
        ("MacroError", "Code") => new("ErrorCode"),
        ("MacroError", "Message") => MacroType.String,
        ("MacroError", "Line" or "Column") => MacroType.Long,
        _ => null,
    };

    internal static object? EnumValue(string type, string name) => type switch
    {
        "DialogButtons" => Enum.TryParse<MacroDialogButtons>(name, out var a) && Enum.GetName(a) == name ? a : null,
        "DialogButton" => Enum.TryParse<MacroDialogButton>(name, out var b) && Enum.GetName(b) == name ? b : null,
        "ErrorCode" => Enum.TryParse<MacroErrorCode>(name, out var e) && Enum.GetName(e) == name ? e : null,
        _ => null,
    };
}

internal sealed class MacroChecker(MacroProgram program, MacroEditorSymbols? editor = null)
{
    private sealed record Variable(MacroType Type, object Declaration, bool ReadOnly);
    private sealed record Scope(Dictionary<string, Variable> Names, MacroBlock Body, bool IncludesHeader);
    private readonly List<Scope> scopes = [];
    private readonly Dictionary<string, MacroConstantsFile> constants = program.Constants.ToDictionary(
        binding => binding.Alias, binding => binding.File, StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> calls = new(StringComparer.Ordinal);
    private string caller = "";
    private MacroType returns = MacroType.Void;
    private int locals, loops;
    private bool insideCatch;
    private static readonly HashSet<string> Keywords = new("if else while for foreach in var true false return break continue try catch void Macro List class using new null switch do finally throw async await namespace ref out goto".Split(' '));

    public void Check()
    {
        foreach (var function in program.Functions.Values) CheckName(function.Name, function.Position);
        CheckFrame("", MacroType.Void, [], program.Body);
        foreach (var function in program.Functions.Values)
        {
            try
            {
                CheckFrame(function.Name, function.Return, function.Parameters, function.Body);
                if (function.Return != MacroType.Void && !Returns(function.Body)) Fail("MissingReturn", function.Position);
            }
            catch (MacroSourceException error) when (editor?.Recover(error) == true) { }
        }
        var visited = new Dictionary<string, int>(); var visiting = new HashSet<string>();
        foreach (string function in program.Functions.Keys) Visit(function);

        int Visit(string name)
        {
            if (visited.TryGetValue(name, out int depth)) return depth;
            if (!visiting.Add(name)) Fail("RecursiveCall", program.Functions[name].Position);
            depth = 1;
            foreach (string next in calls[name]) depth = Math.Max(depth, 1 + Visit(next));
            if (depth > 64) Fail("CallDepthLimit", program.Functions[name].Position);
            visiting.Remove(name); visited[name] = depth; return depth;
        }
    }

    private void CheckFrame(string name, MacroType result, MacroParameter[] parameters, MacroBlock body)
    {
        caller = name; returns = result; locals = loops = 0; insideCatch = false; calls[name] = [];
        scopes.Clear(); Push(body);
        foreach (var parameter in parameters) Declare(parameter.Name, parameter.Type, body.Position, parameter);
        CheckBlock(body); Pop();
    }

    private void CheckBlock(MacroBlock block)
    {
        Push(block);
        foreach (var statement in block.Statements)
        {
            int previousScopes = scopes.Count, previousLoops = loops;
            try { CheckStatement(statement); }
            catch (MacroSourceException error) when (editor?.Recover(error) == true)
            {
                scopes.RemoveRange(previousScopes, scopes.Count - previousScopes);
                loops = previousLoops;
            }
        }
        Pop();
    }

    private void CheckStatement(MacroStatement statement)
    {
        switch (statement)
        {
            case MacroBlock block: CheckBlock(block); break;
            case MacroDeclaration declaration:
                try
                {
                    if (declaration.Value is not MacroMissingExpression)
                    {
                        declaration.Type = Expression(declaration.Value, declaration.Declared);
                        MacroNode initializer = program.IsPostConnection ? declaration.Value : declaration;
                        if (declaration.Declared is { } declared)
                        { Require(declared.Accepts(declaration.Type), initializer); declaration.Type = declared; }
                        Require(declaration.Type != MacroType.Void && !declaration.Type.Name.StartsWith('$') && declaration.Type != MacroType.Meta, initializer);
                    }
                    else declaration.Type = declaration.Declared ?? MacroType.Unknown;
                }
                catch (MacroSourceException error) when (editor?.Recover(error) == true)
                { declaration.Type = declaration.Declared ?? MacroType.Unknown; }
                Declare(declaration.Name, declaration.Type, program.IsPostConnection ? declaration.NamePosition : declaration.Position, declaration); break;
            case MacroAssignment assignment:
                MacroType target = Expression(assignment.Target);
                CheckAssignmentTarget(assignment.Target);
                MacroType value = assignment.Value is null ? MacroType.Long : Expression(assignment.Value, target);
                MacroType assigned = assignment.Operator == "=" ? value : BinaryType(assignment.Operator[..1], target, value, assignment.Position);
                Require(target.Accepts(assigned), assignment); break;
            case MacroInvocation invocation: Expression(invocation.Call); break;
            case MacroIf branch:
                Require(Expression(branch.Condition) == MacroType.Bool, program.IsPostConnection ? branch.Condition : branch);
                CheckBlock(branch.Yes); if (branch.No is { } no) CheckStatement(no); break;
            case MacroWhile loop:
                Require(Expression(loop.Condition) == MacroType.Bool, loop);
                loops++; CheckBlock(loop.Body); loops--; break;
            case MacroFor loop:
                Push(loop.Body, includesHeader: true); if (loop.Initial is { } init) CheckStatement(init);
                if (loop.Condition is { } condition) Require(Expression(condition) == MacroType.Bool, loop);
                loops++; CheckBlock(loop.Body); if (loop.Step is { } step) CheckStatement(step); loops--; Pop(); break;
            case MacroForeach loop:
                MacroType collection = Expression(loop.Collection);
                Require(collection.Name == "List", loop);
                loop.Type = loop.Declared ?? collection.Element!;
                Require(loop.Type.Accepts(collection.Element!), loop);
                Push(loop.Body); Declare(loop.Name, loop.Type, loop.Position, loop, readOnly: true);
                loops++; CheckBlock(loop.Body); loops--; Pop(); break;
            case MacroTry attempt:
                CheckBlock(attempt.Body);
                Push(attempt.Catch); Declare(attempt.ErrorName, new("MacroError"), attempt.Position, attempt);
                bool enclosingCatch = insideCatch;
                insideCatch = true;
                try { CheckBlock(attempt.Catch); }
                finally { insideCatch = enclosingCatch; Pop(); }
                break;
            case MacroFlow { Kind: "throw" } rethrow:
                if (!insideCatch) Fail("RethrowOutsideCatch", rethrow.Position);
                break;
            case MacroFlow flow:
                if (flow.Kind != "return") Require(loops != 0, flow);
                else if (flow.Value is null) Require(returns == MacroType.Void, flow);
                else Require(returns != MacroType.Void && returns.Accepts(Expression(flow.Value, returns)), flow);
                break;
        }
    }

    private MacroType Expression(MacroExpression expression, MacroType? context = null)
    {
        MacroType result;
        switch (expression)
        {
            case MacroMissingExpression when editor is not null:
                throw new MacroSourceException("ExpressionExpected", expression.Position);
            case MacroLiteral literal:
                result = ScalarType(literal.Value); break;
            case MacroName name:
                if (name.Name == "Macro") result = MacroType.Meta;
                else if (MacroType.EnumNames.Contains(name.Name)) result = new("$" + name.Name);
                else
                {
                    var variable = Lookup(name.Name, name.Position);
                    result = variable.Type;
                    editor?.BoundVariable(name, variable.Declaration, result);
                }
                break;
            case MacroUnary unary:
                result = Expression(unary.Value);
                Require(unary.Operator == "!" ? result == MacroType.Bool : result.Numeric, unary); break;
            case MacroBinary binary:
                result = BinaryType(binary.Operator, Expression(binary.Left), Expression(binary.Right), binary.Position); break;
            case MacroCast cast:
                Require(Expression(cast.Value).Numeric, cast); result = cast.Target; break;
            case MacroIndex index:
                MacroType indexed = Expression(index.Target); Require(Expression(index.Index) == MacroType.Long, index);
                Require(indexed == MacroType.String || indexed.Name == "List", index);
                result = indexed == MacroType.String ? MacroType.String : indexed.Element!; break;
            case MacroMember member:
                if (member.Target is MacroName alias && constants.TryGetValue(alias.Name, out var file))
                {
                    if (!file.Values.TryGetValue(member.Name, out var value))
                        throw UnknownMember(member);
                    member.ConstantValue = value;
                    result = ScalarType(value);
                    break;
                }
                MacroType owner = Expression(member.Target);
                if (owner.Name.StartsWith('$'))
                {
                    if (MacroApi.EnumValue(owner.Name[1..], member.Name) is null) throw UnknownMember(member);
                    result = new(owner.Name[1..]);
                }
                else result = MacroApi.Member(owner, member.Name) ?? throw UnknownMember(member);
                break;
            case MacroAlternatives alternatives:
                foreach (var pattern in alternatives.Patterns)
                    Require(Expression(pattern).Name == "WaitPattern", pattern);
                result = MacroApi.Alternatives;
                break;
            case MacroListLiteral list:
                MacroType? element = context?.Name == "List" ? context.Element : null;
                if (element is null)
                {
                    foreach (var item in list.Values)
                    {
                        var type = Expression(item);
                        if (element is null) element = type;
                        else if (element.Numeric && type.Numeric) element = element == MacroType.Double || type == MacroType.Double ? MacroType.Double : MacroType.Long;
                        else Require(element == type, list);
                    }
                }
                if (element is null || element == MacroType.Void || element == MacroType.Meta || element.Name.StartsWith('$')) Fail("ListType", list.Position);
                foreach (var item in list.Values) Require(element!.Accepts(Expression(item, element)), list);
                result = MacroType.List(element!);
                int nesting = 0; for (MacroType? nested = result; nested?.Name == "List"; nested = nested.Element) nesting++;
                if (nesting > 4) Fail("TypeNestingLimit", list.Position);
                break;
            case MacroInterpolation interpolation:
                foreach (var part in interpolation.Parts)
                {
                    var type = Expression(part);
                    Require(type.Numeric || type == MacroType.String || type == MacroType.Bool || type.Name == "Instant" || MacroType.EnumNames.Contains(type.Name), interpolation);
                }
                result = MacroType.String; break;
            case MacroNamedCall call: result = CheckCall(call); break;
            case MacroPositionalCall call: result = CheckCall(call); break;
            default: throw new InvalidOperationException();
        }
        expression.Type = result; return result;
    }

    private MacroType CheckCall(MacroNamedCall call)
    {
        if (call.Arguments.Length == 0) Fail("CallArguments", call.Position);
        foreach (var argument in call.Arguments)
        {
            MacroType type = argument.Parameter.Type;
            if (!type.Accepts(Expression(argument.Value, type))) Fail("TypeMismatch", argument.ValueStart);
        }
        return call.Signature.Result;
    }

    private MacroType CheckCall(MacroPositionalCall call)
    {
        if (program.Functions.TryGetValue(call.Name, out var function))
        {
            Require(function.Parameters.Length == call.Arguments.Length, call);
            for (int i = 0; i < call.Arguments.Length; i++)
                Require(function.Parameters[i].Type.Accepts(Expression(call.Arguments[i], function.Parameters[i].Type)), call);
            calls[caller].Add(call.Name); return function.Return;
        }
        if (!MacroApi.Signatures.TryGetValue(call.Name, out var signatures)) Fail("UnknownFunction", call.Position);
        MacroSourceException? last = null;
        foreach (var signature in signatures!)
        {
            if (signature.Parameters.Length != call.Arguments.Length) continue;
            try
            {
                MacroType? generic = null;
                for (int i = 0; i < call.Arguments.Length; i++)
                {
                    MacroType parameter = Replace(signature.Parameters[i], generic);
                    MacroType argument = Expression(call.Arguments[i], parameter.Name == "T" || parameter.Element?.Name == "T" ? null : parameter);
                    if (parameter.Name == "List" && parameter.Element?.Name == "T")
                    { Require(argument.Name == "List", call); generic = argument.Element; parameter = argument; }
                    else if (parameter.Name == "T") { generic ??= argument; parameter = generic; }
                    if (editor is not null && !program.IsPostConnection && call.Name == "WaitPrompt" && i == 0 && argument == MacroType.String)
                        throw new MacroSourceException("TypeMismatch", call.Position)
                            { WaitPatternFix = editor.FirstArgumentRange(call.Arguments[i]) };
                    Require(parameter.Accepts(argument), program.IsPostConnection ? call.Arguments[i] : call);
                }
                return Replace(signature.Result, generic);
            }
            catch (MacroSourceException error) { last = error; }
        }
        throw last ?? new MacroSourceException("CallArguments", call.Position);
    }

    private static MacroSourceException UnknownMember(MacroMember member) =>
        new("UnknownMember", member.Position, length: member.Name.Length);

    private void CheckAssignmentTarget(MacroExpression target)
    {
        if (target is MacroName variable)
        {
            if (Lookup(variable.Name, variable.Position).ReadOnly) Fail("ReadOnly", target.Position);
            return;
        }
        if (target is MacroIndex index)
        {
            if (index.Target.Type.Name != "List") Fail("ReadOnly", index.Position);
            MacroExpression root = index.Target;
            while (root is MacroIndex parent) root = parent.Target;
            if (root is MacroName or MacroMember { Target: MacroName { Name: "Macro" }, Name: "Args" }) return;
        }
        Fail("AssignmentTarget", target.Position);
    }

    private static MacroType Replace(MacroType type, MacroType? generic) => type.Name == "T" ? generic ?? type :
        type.Name == "List" ? MacroType.List(Replace(type.Element!, generic)) : type;

    private static MacroType BinaryType(string op, MacroType left, MacroType right, MacroPosition at)
    {
        if (op is "&&" or "||")
        { if (left == MacroType.Bool && right == MacroType.Bool) return MacroType.Bool; }
        else if (op is "==" or "!=")
        {
            if (left.Numeric && right.Numeric || left == right && (left == MacroType.Bool || left == MacroType.String || MacroType.EnumNames.Contains(left.Name))) return MacroType.Bool;
        }
        else if (left.Numeric && right.Numeric)
            return op is "<" or "<=" or ">" or ">=" ? MacroType.Bool : left == MacroType.Double || right == MacroType.Double ? MacroType.Double : MacroType.Long;
        else if (op == "+" && left == MacroType.String && right == MacroType.String) return MacroType.String;
        throw new MacroSourceException("TypeMismatch", at);
    }

    private void Declare(string name, MacroType type, MacroPosition at, object declaration, bool readOnly = false)
    {
        CheckName(name, at);
        if (program.Functions.ContainsKey(name) || scopes.Any(scope => scope.Names.ContainsKey(name))) Fail("DuplicateLocal", at);
        if (++locals > 1024) Fail("LocalLimit", at);
        var scope = scopes[^1];
        scope.Names.Add(name, new(type, declaration, readOnly));
        editor?.AcceptLocal(declaration, type, caller, scope.Body, scope.IncludesHeader);
    }
    internal static MacroType ScalarType(object value) => value switch
    {
        bool => MacroType.Bool,
        long => MacroType.Long,
        double => MacroType.Double,
        string => MacroType.String,
        _ => throw new InvalidOperationException(),
    };

    internal static bool IsReserved(string name) =>
        Keywords.Contains(name) || MacroType.ValueNames.Contains(name) || MacroApi.Signatures.ContainsKey(name);

    private void CheckName(string name, MacroPosition at)
    {
        if (IsReserved(name)) Fail("ReservedName", at);
        if (constants.ContainsKey(name)) Fail("ConstantsAlias", at);
    }
    private Variable Lookup(string name, MacroPosition at)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].Names.TryGetValue(name, out var variable)) return variable;
        throw new MacroSourceException("UnknownName", at);
    }
    private void Push(MacroBlock body, bool includesHeader = false) =>
        scopes.Add(new(new(StringComparer.Ordinal), body, includesHeader));
    private void Pop() => scopes.RemoveAt(scopes.Count - 1);
    private static void Require(bool condition, MacroNode node) { if (!condition) Fail("TypeMismatch", node.Position); }
    private static void Fail(string code, MacroPosition at) => throw new MacroSourceException(code, at);
    private static bool Returns(MacroStatement statement) => statement switch
    {
        MacroFlow { Kind: "return" or "throw" } => true,
        MacroBlock b => b.Statements.Any(Returns),
        MacroIf { No: { } no } b => Returns(b.Yes) && Returns(no),
        MacroTry t => Returns(t.Body) && Returns(t.Catch),
        _ => false,
    };
}
