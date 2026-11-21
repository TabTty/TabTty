namespace TabTty.Core;

internal sealed record MacroSignature(MacroType Result, params MacroType[] Parameters);

internal static class MacroApi
{
    internal static readonly Dictionary<string, MacroSignature[]> Signatures = Build();
    private static Dictionary<string, MacroSignature[]> Build()
    {
        var v = MacroType.Void; var s = MacroType.String; var n = MacroType.Long;
        var d = MacroType.Double; var b = MacroType.Bool; var t = new MacroType("T");
        var list = MacroType.List(t); var strings = MacroType.List(s);
        var pattern = new MacroType("WaitPattern"); var match = new MacroType("WaitMatch");
        var instant = new MacroType("Instant");
        var result = new Dictionary<string, MacroSignature[]>(StringComparer.Ordinal);
        void Add(string name, MacroSignature first, MacroSignature? second = null) =>
            result.Add(name, second is null ? [first] : [first, second]);
        result.Add("Connect", [new(v, s), new(v, s, s), new(v, s, s, s), new(v, s, s, s, b)]);
        Add("SetBaseDirectory", new(v, s));
        Add("ResetBaseDirectory", new(v)); Add("BaseDirectory", new(s));
        Add("GetParentDirectory", new(s, s)); Add("GetFileBaseName", new(s, s)); Add("GetFileExtension", new(s, s));
        Add("Write", new(v, s)); Add("WriteLine", new(v, s));
        Add("WritePassword", new(v), new(v, s)); Add("WritePasswordLine", new(v), new(v, s));
        Add("MarkReceiveBoundary", new(v));
        Add("Literal", new(pattern, s), new(pattern, s, b)); Add("Regex", new(pattern, s), new(pattern, s, b));
        foreach (string name in new[] { "WaitLine", "WaitPrompt" }) Add(name, new(match, pattern), new(match, pattern, n));
        foreach (string name in new[] { "WaitAnyLine", "WaitAnyPrompt" })
            Add(name, new(match, MacroType.List(pattern)), new(match, MacroType.List(pattern), n));
        Add("Sleep", new(v, n)); Add("MinimizeWindow", new(v)); Add("RestoreWindow", new(v));
        Add("Dialog", new(new("DialogResult"), s, s, new("DialogButtons")),
            new(new("DialogResult"), s, s, new("DialogButtons"), b, s));
        Add("Length", new(n, s)); Add("Count", new(n, list)); Add("Substring", new(s, s, n, n));
        foreach (string name in new[] { "Contains", "StartsWith", "EndsWith" }) Add(name, new(b, s, s), new(b, s, s, b));
        Add("IndexOf", new(n, s, s), new(n, s, s, b)); Add("Replace", new(s, s, s, s));
        Add("Split", new(strings, s, s)); Add("Join", new(s, s, strings));
        foreach (string name in new[] { "Trim", "ToUpperInvariant", "ToLowerInvariant" }) Add(name, new(s, s));
        Add("Add", new(v, list, t)); Add("Insert", new(v, list, n, t));
        Add("RemoveAt", new(v, list, n)); Add("Clear", new(v, list)); Add("Clone", new(list, list));
        Add("ParseLong", new(n, s)); Add("ParseDouble", new(d, s));
        Add("FormatNumber", new(s, n, s), new(s, d, s));
        Add("Now", new(instant)); Add("UtcNow", new(instant));
        Add("FormatTime", new(s, instant), new(s, instant, s));
        Add("UnixMilliseconds", new(n, instant)); Add("ElapsedMilliseconds", new(n));
        return result;
    }

    internal static MacroType? Member(MacroType target, string name) => (target.Name, name) switch
    {
        ("Macro", "Args") => MacroType.List(MacroType.String),
        ("Macro", "FullPath" or "FileName" or "BaseName" or "RunId") => MacroType.String,
        ("WaitMatch", "AlternativeIndex" or "Index" or "Length") => MacroType.Long,
        ("WaitMatch", "Text") => MacroType.String,
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

internal sealed class MacroChecker(MacroProgram program)
{
    private sealed record Variable(MacroType Type, bool ReadOnly = false);
    private readonly List<Dictionary<string, Variable>> scopes = [];
    private readonly Dictionary<string, MacroConstantsFile> constants = program.Constants.ToDictionary(
        binding => binding.Alias, binding => binding.File, StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> calls = new(StringComparer.Ordinal);
    private string caller = "";
    private MacroType returns = MacroType.Void;
    private int locals, loops;
    private static readonly HashSet<string> Keywords = new("if else while for foreach in var true false return break continue try catch void Macro List class using new null switch do finally throw async await namespace ref out goto".Split(' '));

    public void Check()
    {
        foreach (var function in program.Functions.Values) CheckName(function.Name, function.Position);
        CheckFrame("", MacroType.Void, [], program.Body);
        foreach (var function in program.Functions.Values)
        {
            CheckFrame(function.Name, function.Return, function.Parameters, function.Body);
            if (function.Return != MacroType.Void && !Returns(function.Body)) Fail("MissingReturn", function.Position);
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
        caller = name; returns = result; locals = loops = 0; calls[name] = [];
        scopes.Clear(); Push();
        foreach (var parameter in parameters) Declare(parameter.Name, parameter.Type, body.Position);
        CheckBlock(body); Pop();
    }

    private void CheckBlock(MacroBlock block)
    { Push(); foreach (var statement in block.Statements) CheckStatement(statement); Pop(); }

    private void CheckStatement(MacroStatement statement)
    {
        switch (statement)
        {
            case MacroBlock block: CheckBlock(block); break;
            case MacroDeclaration declaration:
                declaration.Type = Expression(declaration.Value, declaration.Declared);
                if (declaration.Declared is { } declared)
                { Require(declared.Accepts(declaration.Type), declaration); declaration.Type = declared; }
                Require(declaration.Type != MacroType.Void && !declaration.Type.Name.StartsWith('$') && declaration.Type != MacroType.Meta, declaration);
                Declare(declaration.Name, declaration.Type, declaration.Position); break;
            case MacroAssignment assignment:
                if (assignment.Target is not MacroName && assignment.Target is not MacroIndex { Target: MacroName }) Fail("AssignmentTarget", assignment.Position);
                MacroType target = Expression(assignment.Target);
                if (assignment.Target is MacroName identifier && Lookup(identifier.Name, identifier.Position).ReadOnly) Fail("ReadOnly", assignment.Position);
                if (assignment.Target is MacroIndex index && Expression(index.Target).Name != "List") Fail("ReadOnly", assignment.Position);
                MacroType value = assignment.Value is null ? MacroType.Long : Expression(assignment.Value, target);
                MacroType assigned = assignment.Operator == "=" ? value : BinaryType(assignment.Operator[..1], target, value, assignment.Position);
                Require(target.Accepts(assigned), assignment); break;
            case MacroInvocation invocation: Expression(invocation.Call); break;
            case MacroIf branch:
                Require(Expression(branch.Condition) == MacroType.Bool, branch);
                CheckBlock(branch.Yes); if (branch.No is { } no) CheckStatement(no); break;
            case MacroWhile loop:
                Require(Expression(loop.Condition) == MacroType.Bool, loop);
                loops++; CheckBlock(loop.Body); loops--; break;
            case MacroFor loop:
                Push(); if (loop.Initial is { } init) CheckStatement(init);
                if (loop.Condition is { } condition) Require(Expression(condition) == MacroType.Bool, loop);
                loops++; CheckBlock(loop.Body); if (loop.Step is { } step) CheckStatement(step); loops--; Pop(); break;
            case MacroForeach loop:
                MacroType collection = Expression(loop.Collection);
                Require(collection.Name == "List", loop);
                loop.Type = loop.Declared ?? collection.Element!;
                Require(loop.Type.Accepts(collection.Element!), loop);
                Push(); Declare(loop.Name, loop.Type, loop.Position, readOnly: true);
                loops++; CheckBlock(loop.Body); loops--; Pop(); break;
            case MacroTry attempt:
                CheckBlock(attempt.Body);
                Push(); Declare(attempt.ErrorName, new("MacroError"), attempt.Position);
                CheckBlock(attempt.Catch); Pop(); break;
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
            case MacroLiteral literal:
                result = ScalarType(literal.Value); break;
            case MacroName name:
                result = name.Name == "Macro" ? MacroType.Meta : MacroType.EnumNames.Contains(name.Name)
                    ? new("$" + name.Name) : Lookup(name.Name, name.Position).Type; break;
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
                        throw new MacroSourceException("UnknownMember", member.Position);
                    member.ConstantValue = value;
                    result = ScalarType(value);
                    break;
                }
                MacroType owner = Expression(member.Target);
                if (owner.Name.StartsWith('$'))
                {
                    if (MacroApi.EnumValue(owner.Name[1..], member.Name) is null) Fail("UnknownMember", member.Position);
                    result = new(owner.Name[1..]);
                }
                else result = MacroApi.Member(owner, member.Name) ?? throw new MacroSourceException("UnknownMember", member.Position);
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
            case MacroCall call: result = CheckCall(call); break;
            default: throw new InvalidOperationException();
        }
        expression.Type = result; return result;
    }

    private MacroType CheckCall(MacroCall call)
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
                    Require(parameter.Accepts(argument), call);
                }
                return Replace(signature.Result, generic);
            }
            catch (MacroSourceException error) { last = error; }
        }
        throw last ?? new MacroSourceException("CallArguments", call.Position);
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

    private void Declare(string name, MacroType type, MacroPosition at, bool readOnly = false)
    {
        CheckName(name, at);
        if (program.Functions.ContainsKey(name) || scopes.Any(scope => scope.ContainsKey(name))) Fail("DuplicateLocal", at);
        if (++locals > 1024) Fail("LocalLimit", at);
        scopes[^1].Add(name, new(type, readOnly));
    }
    private static MacroType ScalarType(object value) => value switch
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
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].TryGetValue(name, out var variable)) return variable;
        throw new MacroSourceException("UnknownName", at);
    }
    private void Push() => scopes.Add(new(StringComparer.Ordinal));
    private void Pop() => scopes.RemoveAt(scopes.Count - 1);
    private static void Require(bool condition, MacroNode node) { if (!condition) Fail("TypeMismatch", node.Position); }
    private static void Fail(string code, MacroPosition at) => throw new MacroSourceException(code, at);
    private static bool Returns(MacroStatement statement) => statement switch
    {
        MacroFlow { Kind: "return" } => true,
        MacroBlock b => b.Statements.Any(Returns),
        MacroIf { No: { } no } b => Returns(b.Yes) && Returns(no),
        MacroTry t => Returns(t.Body) && Returns(t.Catch),
        _ => false,
    };
}
