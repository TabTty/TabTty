namespace TabTty.Core;

internal static class PostConnectionMacro
{
    internal static readonly string[] LocalTypes = ["bool", "long", "string", "WaitPattern", "WaitMatch"];
    // The ordinary API supplies the parameter and result types. The embedded waits require a timeout.
    internal static readonly Dictionary<string, int> Calls = new(StringComparer.Ordinal)
    {
        ["Literal"] = 1,
        ["Regex"] = 1,
        ["WaitAnyPrompt"] = 2,
        ["WaitPrompt"] = 2,
        ["WriteLine"] = 1,
        ["WritePasswordLine"] = 0,
    };

    internal static MacroSignature? Signature(string name) =>
        Calls.ContainsKey(name) ? MacroApi.Signatures[name][^1] : null;

    internal static void Check(MacroBlock body)
    {
        var remaining = new Stack<MacroNode>();
        remaining.Push(body);
        while (remaining.TryPop(out var node))
        {
            switch (node)
            {
                case MacroBlock block:
                    PushAll(block.Statements);
                    break;
                case MacroDeclaration declaration when declaration.Declared is null ||
                    LocalTypes.Contains(declaration.Declared.Name):
                    remaining.Push(declaration.Value);
                    break;
                case MacroIf branch:
                    if (branch.No is not null) remaining.Push(branch.No);
                    remaining.Push(branch.Yes);
                    remaining.Push(branch.Condition);
                    break;
                case MacroInvocation call:
                    remaining.Push(call.Call);
                    break;
                case MacroNamedCall call when Calls.ContainsKey(call.Name):
                    PushAll(call.Arguments.Select(argument => argument.Value).ToArray());
                    break;
                case MacroPositionalCall call when Calls.TryGetValue(call.Name, out var definition) &&
                    call.Arguments.Length >= definition:
                    PushAll(call.Arguments);
                    break;
                case MacroAlternatives choices:
                    PushAll(choices.Patterns);
                    break;
                case MacroMember { Name: "AlternativeName" } member:
                    remaining.Push(member.Target);
                    break;
                case MacroUnary { Operator: "!" } unary:
                    remaining.Push(unary.Value);
                    break;
                case MacroUnary { Operator: "-", Value: MacroLiteral { Value: long } }:
                    break;
                case MacroBinary { Operator: "==" or "!=" or "&&" or "||" } binary:
                    remaining.Push(binary.Right);
                    remaining.Push(binary.Left);
                    break;
                case MacroFlow { Kind: "return", Value: null }:
                case MacroLiteral { Value: string or long or bool }:
                case MacroName name when name.Name != "Macro" && !MacroType.EnumNames.Contains(name.Name):
                    break;
                default:
                    throw new MacroSourceException("PostConnectionSubset", node.Position);
            }
        }

        void PushAll(IReadOnlyList<MacroNode> nodes)
        {
            for (int index = nodes.Count - 1; index >= 0; index--)
                remaining.Push(nodes[index]);
        }
    }
}
