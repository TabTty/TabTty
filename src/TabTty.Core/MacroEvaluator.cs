namespace TabTty.Core;

internal sealed partial class MacroRun
{
    private List<Dictionary<string, object>> scopes = [];
    private MacroError? caughtError;
    private sealed record Flow(string Kind, object? Value = null);
    private sealed record EnumRoot(string Name);
    private static readonly object VoidValue = new();

    private async Task ExecuteProgramAsync()
    { await ExecuteBlockAsync(Program.Body).ConfigureAwait(false); }

    private async ValueTask<Flow?> ExecuteBlockAsync(MacroBlock block)
    {
        PushScope();
        try
        {
            foreach (var statement in block.Statements)
                if (await ExecuteStatementAsync(statement).ConfigureAwait(false) is { } flow) return flow;
            return null;
        }
        finally { PopScope(); }
    }

    private async ValueTask<Flow?> ExecuteStatementAsync(MacroStatement statement)
    {
        await CheckpointAsync(statement.Position).ConfigureAwait(false);
        try
        {
            switch (statement)
            {
                case MacroBlock block: return await ExecuteBlockAsync(block).ConfigureAwait(false);
                case MacroDeclaration declaration:
                    Declare(declaration.Name, ConvertValue(await ValueAsync(declaration.Value).ConfigureAwait(false), declaration.Type)); break;
                case MacroAssignment assignment:
                    MacroList? list = null; Dictionary<string, object>? scope = null; int index = 0; string name = "";
                    object previous;
                    if (assignment.Target is MacroName identifier)
                    { name = identifier.Name; scope = FindScope(name); previous = scope[name]; }
                    else
                    {
                        var target = (MacroIndex)assignment.Target;
                        list = (MacroList)await ValueAsync(target.Target).ConfigureAwait(false);
                        index = Index(await ValueAsync(target.Index).ConfigureAwait(false), list.Items.Count, at: target.Position);
                        previous = list.Items[index];
                    }
                    object right = assignment.Value is null ? 1L : await ValueAsync(assignment.Value).ConfigureAwait(false);
                    object value = ConvertValue(assignment.Operator == "=" ? right : Binary(assignment.Operator[..1], previous, right), assignment.Target.Type);
                    Own(value);
                    if (list is not null)
                    {
                        Index((long)index, list.Items.Count, at: assignment.Target.Position);
                        list.Items[index] = value;
                        list.Revision++;
                    }
                    else scope![name] = value;
                    break;
                case MacroInvocation call: await ValueAsync(call.Call).ConfigureAwait(false); break;
                case MacroIf branch:
                    if ((bool)await ValueAsync(branch.Condition).ConfigureAwait(false)) return await ExecuteBlockAsync(branch.Yes).ConfigureAwait(false);
                    if (branch.No is { } no) return await ExecuteStatementAsync(no).ConfigureAwait(false);
                    break;
                case MacroWhile loop:
                    while ((bool)await ValueAsync(loop.Condition).ConfigureAwait(false))
                    {
                        var flow = await ExecuteBlockAsync(loop.Body).ConfigureAwait(false);
                        if (flow?.Kind == "return") return flow;
                        if (flow?.Kind == "break") break;
                    }
                    break;
                case MacroFor loop:
                    PushScope();
                    try
                    {
                        if (loop.Initial is { } init) await ExecuteStatementAsync(init).ConfigureAwait(false);
                        while (loop.Condition is null || (bool)await ValueAsync(loop.Condition).ConfigureAwait(false))
                        {
                            await CheckpointAsync(loop.Position).ConfigureAwait(false);
                            var flow = await ExecuteBlockAsync(loop.Body).ConfigureAwait(false);
                            if (flow?.Kind == "return") return flow;
                            if (flow?.Kind == "break") break;
                            if (loop.Step is { } step) await ExecuteStatementAsync(step).ConfigureAwait(false);
                        }
                    }
                    finally { PopScope(); }
                    break;
                case MacroForeach loop:
                    var collection = (MacroList)await ValueAsync(loop.Collection).ConfigureAwait(false);
                    long revision = collection.Revision;
                    for (int i = 0; ; i++)
                    {
                        await CheckpointAsync(loop.Position).ConfigureAwait(false);
                        if (revision != collection.Revision) throw new MacroError(MacroErrorCode.CollectionModified);
                        if (i == collection.Items.Count) break;
                        PushScope();
                        try
                        {
                            Declare(loop.Name, ConvertValue(collection.Items[i], loop.Type));
                            var flow = await ExecuteBlockAsync(loop.Body).ConfigureAwait(false);
                            if (flow?.Kind == "return") return flow;
                            if (flow?.Kind == "break") break;
                        }
                        finally { PopScope(); }
                    }
                    break;
                case MacroTry attempt:
                    try { return await ExecuteBlockAsync(attempt.Body).ConfigureAwait(false); }
                    catch (MacroError error)
                    {
                        await CheckpointAsync(attempt.Position).ConfigureAwait(false);
                        PushScope();
                        var enclosingError = caughtError;
                        caughtError = error;
                        try
                        {
                            error.CaughtMessage ??= host.ErrorMessage(error.Code);
                            Declare(attempt.ErrorName, error);
                            return await ExecuteBlockAsync(attempt.Catch).ConfigureAwait(false);
                        }
                        finally { caughtError = enclosingError; PopScope(); }
                    }
                case MacroFlow { Kind: "throw" }:
                    throw caughtError!;
                case MacroFlow flow:
                    return new(flow.Kind, flow.Value is null ? null : await ValueAsync(flow.Value).ConfigureAwait(false));
            }
            return null;
        }
        catch (MacroError error) { if (error.Position.Line == 0) error.Position = statement.Position; throw; }
        catch (OverflowException) { throw new MacroError(MacroErrorCode.NumericOverflow) { Position = statement.Position }; }
    }

    private async ValueTask<object> ValueAsync(MacroExpression expression)
    {
        await CheckpointAsync(expression.Position).ConfigureAwait(false);
        object result;
        try
        {
            switch (expression)
            {
                case MacroLiteral literal: result = literal.Value; break;
                case MacroName name:
                    result = name.Name == "Macro" ? this : MacroType.EnumNames.Contains(name.Name) ? new EnumRoot(name.Name) : FindScope(name.Name)[name.Name]; break;
                case MacroUnary unary:
                    var operand = await ValueAsync(unary.Value).ConfigureAwait(false);
                    result = unary.Operator switch { "!" => !(bool)operand, "+" => operand,
                        _ => operand is long unaryNumber ? (object)checked(-unaryNumber) : Finite(-(double)operand) }; break;
                case MacroBinary binary:
                    var left = await ValueAsync(binary.Left).ConfigureAwait(false);
                    if (binary.Operator == "&&" && !(bool)left) result = false;
                    else if (binary.Operator == "||" && (bool)left) result = true;
                    else result = Binary(binary.Operator, left, await ValueAsync(binary.Right).ConfigureAwait(false));
                    break;
                case MacroCast cast:
                    var number = await ValueAsync(cast.Value).ConfigureAwait(false);
                    if (cast.Target == MacroType.Double) result = number is long integral ? (double)integral : number;
                    else if (number is long) result = number;
                    else
                    {
                        double floating = (double)number;
                        if (!double.IsFinite(floating) || floating < -9223372036854775808d || floating >= 9223372036854775808d)
                            throw new MacroError(MacroErrorCode.NumericOverflow);
                        result = (long)floating;
                    }
                    break;
                case MacroAlternatives alternatives:
                    if (alternatives.Patterns.Length > 16) throw new MacroError(MacroErrorCode.InvalidArgument);
                    var candidates = new MacroCandidate[alternatives.Patterns.Length];
                    for (int i = 0; i < candidates.Length; i++)
                        candidates[i] = new(alternatives.Names[i], (MacroPattern)await ValueAsync(alternatives.Patterns[i]).ConfigureAwait(false));
                    result = candidates;
                    break;
                case MacroListLiteral literal:
                    var values = new List<object>();
                    if (literal.Values.Length > MacroLimits.ListItems) throw new MacroError(MacroErrorCode.PayloadLimit);
                    foreach (var item in literal.Values) values.Add(ConvertValue(await ValueAsync(item).ConfigureAwait(false), literal.Type.Element!));
                    result = new MacroList(literal.Type.Element!, values); break;
                case MacroIndex index:
                    var target = await ValueAsync(index.Target).ConfigureAwait(false);
                    var offset = await ValueAsync(index.Index).ConfigureAwait(false);
                    result = target is string text ? text[Index(offset, text.Length)].ToString() : ((MacroList)target).Items[Index(offset, ((MacroList)target).Items.Count)]; break;
                case MacroMember member:
                    result = member.ConstantValue ?? Member(await ValueAsync(member.Target).ConfigureAwait(false), member.Name); break;
                case MacroInterpolation interpolation:
                    var builder = new System.Text.StringBuilder();
                    foreach (var part in interpolation.Parts)
                    {
                        string partText = MacroLimits.Format(await ValueAsync(part).ConfigureAwait(false));
                        if (partText.Length > MacroLimits.StringUnits - builder.Length) throw new MacroError(MacroErrorCode.PayloadLimit);
                        builder.Append(partText);
                    }
                    result = builder.ToString(); break;
                case MacroCall call:
                    object[] args = new object[call.ParameterSlots is null ? call.Arguments.Length : MacroApi.NamedParameters[call.Name].Length];
                    for (int i = 0; i < call.Arguments.Length; i++)
                        args[call.ParameterSlots?[i].Index ?? i] = await ValueAsync(call.Arguments[i]).ConfigureAwait(false);
                    // Arguments may have suspended or called helpers. The call owns its own source location.
                    await CheckpointAsync(call.Position).ConfigureAwait(false);
                    if (Program.Functions.TryGetValue(call.Name, out var function))
                    {
                        var callerScopes = scopes; scopes = [];
                        PushScope();
                        try
                        {
                            for (int i = 0; i < args.Length; i++) Declare(function.Parameters[i].Name, ConvertValue(args[i], function.Parameters[i].Type));
                            var flow = await ExecuteBlockAsync(function.Body).ConfigureAwait(false);
                            result = function.Return == MacroType.Void ? VoidValue : ConvertValue(flow!.Value!, function.Return);
                        }
                        finally { PopScope(); scopes = callerScopes; }
                    }
                    else result = await BuiltinAsync(call.Name, args).ConfigureAwait(false);
                    await CheckpointAsync(call.Position).ConfigureAwait(false);
                    break;
                default: throw new InvalidOperationException();
            }
            Own(result); return result;
        }
        catch (MacroError error) { if (error.Position.Line == 0) error.Position = expression.Position; throw; }
        catch (OverflowException) { throw new MacroError(MacroErrorCode.NumericOverflow) { Position = expression.Position }; }
    }

    private object Member(object target, string name) => target switch
    {
        EnumRoot root => MacroApi.EnumValue(root.Name, name)!,
        MacroRun => name switch
        {
            "FullPath" => Program.FullPath, "FileName" => Path.GetFileName(Program.FullPath),
            "BaseName" => BaseName, "RunId" => RunId, "Args" => arguments,
            _ => throw new InvalidOperationException(),
        },
        MacroMatch m => name switch { "AlternativeName" => m.AlternativeName, "Text" => m.Text, "Index" => m.Index,
            "Length" => m.Length, "Groups" => new MacroList(new("CaptureGroup"), m.Groups), _ => throw new InvalidOperationException() },
        MacroCapture g => name switch { "Number" => g.Number, "Name" => g.Name, "Success" => g.Success, "Value" => g.Value,
            "Index" => g.Index, "Length" => g.Length, "Captures" => new MacroList(MacroType.String, g.Captures), _ => throw new InvalidOperationException() },
        MacroDialogResult d => name switch { "Button" => d.Button, "HasText" => d.HasText,
            "Text" => d.HasText ? d.Text : throw new MacroError(MacroErrorCode.NoTextValue), _ => throw new InvalidOperationException() },
        MacroError e => name switch { "Code" => e.Code, "Message" => e.CaughtMessage!,
            "Line" => (long)e.Position.Line, "Column" => (long)e.Position.Column, _ => throw new InvalidOperationException() },
        _ => throw new InvalidOperationException(),
    };

    private void PushScope() { var scope = new Dictionary<string, object>(StringComparer.Ordinal); scopes.Add(scope); Own(scope); }
    private void PopScope() => scopes.RemoveAt(scopes.Count - 1);
    private void Declare(string name, object value) { Own(value); scopes[^1].Add(name, value); Own(scopes[^1]); }
    private Dictionary<string, object> FindScope(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].ContainsKey(name)) return scopes[i];
        throw new InvalidOperationException();
    }
    private static object ConvertValue(object value, MacroType type) => type == MacroType.Double && value is long number ? (double)number : value;
    private static int Index(object value, int length, bool insert = false, MacroPosition at = default)
    {
        long index = (long)value;
        if (index < 0 || index >= (long)length + (insert ? 1 : 0))
            throw new MacroError(MacroErrorCode.IndexOutOfRange) { Position = at };
        return (int)index;
    }
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new MacroError(MacroErrorCode.NumericOverflow);

    private static object Binary(string op, object left, object right)
    {
        if (op is "&&" or "||") return op == "&&" ? (bool)left && (bool)right : (bool)left || (bool)right;
        if (left is string first && right is string second)
        {
            if (op != "+") return op == "==" ? first == second : first != second;
            if (second.Length > MacroLimits.StringUnits - first.Length) throw new MacroError(MacroErrorCode.PayloadLimit);
            return first + second;
        }
        if (left is long a && right is long b)
        {
            if (op is "/" or "%" && b == 0) throw new MacroError(MacroErrorCode.DivideByZero);
            return op switch
            {
                "+" => checked(a + b), "-" => checked(a - b), "*" => checked(a * b), "/" => checked(a / b),
                "%" => a == long.MinValue && b == -1 ? 0L : a % b, "==" => a == b, "!=" => a != b,
                "<" => a < b, "<=" => a <= b, ">" => a > b, ">=" => a >= b, _ => throw new InvalidOperationException(),
            };
        }
        if (left is long or double && right is long or double)
        {
            double a2 = left is long x ? x : (double)left, b2 = right is long y ? y : (double)right;
            if (op is "/" or "%" && b2 == 0) throw new MacroError(MacroErrorCode.DivideByZero);
            return op switch
            {
                "+" => Finite(a2 + b2), "-" => Finite(a2 - b2), "*" => Finite(a2 * b2), "/" => Finite(a2 / b2), "%" => Finite(a2 % b2),
                "==" => a2 == b2, "!=" => a2 != b2, "<" => a2 < b2, "<=" => a2 <= b2, ">" => a2 > b2, ">=" => a2 >= b2,
                _ => throw new InvalidOperationException(),
            };
        }
        return op == "==" ? Equals(left, right) : !Equals(left, right);
    }
}
