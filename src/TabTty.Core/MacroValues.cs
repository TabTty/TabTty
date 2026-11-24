using System.Globalization;

namespace TabTty.Core;

internal enum MacroErrorCode
{
    InvalidArgument, NumericOverflow, DivideByZero, NumberFormat, Format, IndexOutOfRange,
    CollectionModified, NoTextValue, ProfileLoad, ConnectionAlreadyAttempted, ConnectionFailed,
    UserCancelled, NotConnected, Path, MissingProfilePassword, CredentialUnavailable,
    CredentialKindMismatch, VaultStale, VaultUnlockCancelled, WaitTimeout,
    EndOfInput, InvalidPattern, RegexTimeout, ZeroLengthMatch, CaptureLimit, MatchWorkLimit,
    Encoding, PayloadLimit, PatternLimit, OperationUnavailable,
}

internal readonly record struct MacroPosition(int Line, int Column);
internal readonly record struct MacroSourceRange(int Start, int End);

internal sealed class MacroSourceException(string code, MacroPosition position, string? filePath = null, int length = 1) : Exception(code)
{
    public string Code { get; } = code;
    public MacroPosition Position { get; } = position;
    public string? FilePath { get; } = filePath;
    public int Length { get; } = length;
    internal MacroSourceRange? WaitPatternFix { get; init; }
}

internal sealed class MacroError(MacroErrorCode code, string? displayDetail = null) : Exception(code.ToString())
{
    public MacroErrorCode Code { get; } = code;
    public MacroPosition Position { get; set; }
    internal string? DisplayDetail { get; } = displayDetail;
    internal string? CaughtMessage { get; set; }
}

internal sealed class MacroTermination(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

internal enum MacroDialogButtons { OK, OKCancel, YesNo, YesNoCancel }
internal enum MacroDialogButton { OK, Cancel, Yes, No }
internal sealed record MacroDialogRequest(string Title, string Message, MacroDialogButtons Buttons,
    bool InputEnabled, string InitialText);
internal sealed record MacroDialogResult(MacroDialogButton Button, bool HasText, string Text);
internal sealed record MacroCapture(long Number, string Name, bool Success, string Value, long Index,
    long Length, string[] Captures);
internal readonly record struct MacroCandidate(string Name, MacroPattern Pattern);
internal sealed record MacroMatch(string AlternativeName, string Text, long Index, long Length, MacroCapture[] Groups);

internal sealed record MacroType(string Name, MacroType? Element = null)
{
    public static readonly MacroType Void = new("void"), Bool = new("bool"), Long = new("long"),
        Double = new("double"), String = new("string"), Unknown = new("?"), Meta = new("Macro");
    public static MacroType List(MacroType item) => new("List", item);
    public bool Numeric => this == Long || this == Double;
    public bool Accepts(MacroType other) => this == other || this == Double && other == Long;
    public override string ToString() => Element is null ? Name : $"List<{Element}>";
    public static readonly string[] ValueNames = ["bool", "long", "double", "string", "Instant",
        "WaitPattern", "WaitMatch", "CaptureGroup", "DialogResult", "MacroError",
        "ErrorCode", "DialogButtons", "DialogButton"];
    public static readonly string[] EnumNames = ["ErrorCode", "DialogButtons", "DialogButton"];
}

internal sealed class MacroList(MacroType element, IEnumerable<object> values)
{
    public MacroType Element { get; } = element;
    public List<object> Items { get; } = new(values);
    public long Revision { get; set; }
}

internal static class MacroLimits
{
    internal readonly record struct ArgumentProblem(string Code, long Excess = 0, int Row = 0);

    internal static ArgumentProblem? CheckArguments(IReadOnlyList<string> arguments)
    {
        const int countLimit = 64, rowLimit = 4096, totalLimit = 16384;
        if (arguments.Count > countLimit) return new("MacroArgumentCountLimit", arguments.Count - countLimit);
        long total = 0;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is null) return new("MacroArgumentInvalid", Row: index + 1);
            if (argument.Length > rowLimit) return new("MacroArgumentRowLimit", argument.Length - rowLimit, index + 1);
            try { Unicode(argument); }
            catch (MacroError) { return new("MacroArgumentInvalid", Row: index + 1); }
            total += argument.Length;
        }
        return total > totalLimit ? new("MacroArgumentTotalLimit", total - totalLimit) : null;
    }

    internal static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        if (CheckArguments(arguments) is { } problem)
            throw new MacroSourceException(problem.Excess == 0 ? "InvalidArgument" : "ArgumentLimit", new(1, 1));
    }

    public const int SourceBytes = 1024 * 1024, StringUnits = 1024 * 1024, ListItems = 65536,
        LineUnits = 65536, HistoryUnits = 1024 * 1024, HistoryLines = 65536;
    public const long RunBytes = 32L * 1024 * 1024, ReserveBytes = 8L * 1024 * 1024;
    public static readonly ResourceQuota ApplicationMemory = new(256L * 1024 * 1024);
    public static readonly SemaphoreSlim Workers = new(2);
    public static readonly SemaphoreSlim Runs = new(16);

    public static string String(string text)
    {
        if (text.Length > StringUnits) throw new MacroError(MacroErrorCode.PayloadLimit);
        return text;
    }

    public static void Unicode(string text, bool allowNul = false)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!allowNul && c == '\0') throw new MacroError(MacroErrorCode.InvalidArgument);
            if (!char.IsSurrogate(c)) continue;
            if (!char.IsHighSurrogate(c) || ++i == text.Length || !char.IsLowSurrogate(text[i]))
                throw new MacroError(MacroErrorCode.InvalidArgument);
        }
    }

    public static string Format(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        long n => n.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        MacroDialogButtons or MacroDialogButton or MacroErrorCode => value.ToString()!,
        _ => throw new MacroError(MacroErrorCode.InvalidArgument),
    };
}

internal abstract record MacroNode(MacroPosition Position);
internal abstract record MacroExpression(MacroPosition Position) : MacroNode(Position)
{
    public MacroType Type { get; set; } = MacroType.Unknown;
    public int Depth { get; set; } = 1;
}
internal sealed record MacroLiteral(object Value, MacroPosition At) : MacroExpression(At);
internal sealed record MacroMissingExpression(MacroPosition At) : MacroExpression(At);
internal sealed record MacroName(string Name, MacroPosition At) : MacroExpression(At);
internal sealed record MacroUnary(string Operator, MacroExpression Value, MacroPosition At) : MacroExpression(At);
internal sealed record MacroBinary(string Operator, MacroExpression Left, MacroExpression Right, MacroPosition At) : MacroExpression(At);
internal sealed record MacroCast(MacroType Target, MacroExpression Value, MacroPosition At) : MacroExpression(At);
internal readonly record struct MacroBoundArgument(MacroExpression Value, MacroBuiltinParameter Parameter, MacroPosition ValueStart);
internal abstract record MacroCall(string Name, MacroPosition At) : MacroExpression(At);
internal sealed record MacroPositionalCall(string Name, MacroExpression[] Arguments, MacroPosition At) : MacroCall(Name, At);
internal sealed record MacroNamedCall(string Name, MacroSignature Signature, MacroBoundArgument[] Arguments, MacroPosition At)
    : MacroCall(Name, At);
internal sealed record MacroAlternatives(string[] Names, MacroExpression[] Patterns, MacroPosition At) : MacroExpression(At);
internal sealed record MacroMember(MacroExpression Target, string Name, MacroPosition At) : MacroExpression(At)
{
    public object? ConstantValue { get; set; }
}
internal sealed record MacroIndex(MacroExpression Target, MacroExpression Index, MacroPosition At) : MacroExpression(At);
internal sealed record MacroListLiteral(MacroExpression[] Values, MacroPosition At) : MacroExpression(At);
internal sealed record MacroInterpolation(MacroExpression[] Parts, MacroPosition At) : MacroExpression(At);
internal abstract record MacroStatement(MacroPosition Position) : MacroNode(Position);
internal sealed record MacroBlock(MacroStatement[] Statements, MacroPosition At) : MacroStatement(At);
internal sealed record MacroDeclaration(string Name, MacroType? Declared, MacroExpression Value, MacroPosition At,
    MacroPosition NamePosition) : MacroStatement(At)
{
    public MacroType Type { get; set; } = MacroType.Unknown;
}
internal sealed record MacroAssignment(MacroExpression Target, string Operator, MacroExpression? Value, MacroPosition At) : MacroStatement(At);
internal sealed record MacroInvocation(MacroCall Call, MacroPosition At) : MacroStatement(At);
internal sealed record MacroIf(MacroExpression Condition, MacroBlock Yes, MacroStatement? No, MacroPosition At) : MacroStatement(At);
internal sealed record MacroWhile(MacroExpression Condition, MacroBlock Body, MacroPosition At) : MacroStatement(At);
internal sealed record MacroFor(MacroStatement? Initial, MacroExpression? Condition, MacroStatement? Step, MacroBlock Body, MacroPosition At) : MacroStatement(At);
internal sealed record MacroForeach(string Name, MacroType? Declared, MacroExpression Collection, MacroBlock Body, MacroPosition At) : MacroStatement(At)
{
    public MacroType Type { get; set; } = MacroType.Unknown;
}
internal sealed record MacroTry(MacroBlock Body, string ErrorName, MacroBlock Catch, MacroPosition At) : MacroStatement(At);
internal sealed record MacroFlow(string Kind, MacroExpression? Value, MacroPosition At) : MacroStatement(At);
internal sealed record MacroParameter(string Name, MacroType Type);
internal sealed record MacroFunction(string Name, MacroType Return, MacroParameter[] Parameters, MacroBlock Body, MacroPosition Position);
