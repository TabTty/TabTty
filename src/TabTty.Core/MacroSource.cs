using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal sealed class MacroProgram
{
    private MacroProgram(string fullPath, string source, MacroBlock body, Dictionary<string, MacroFunction> functions,
        int nodes)
    { FullPath = fullPath; Source = source; Body = body; Functions = functions; NodeCount = nodes; }
    public string FullPath { get; }
    internal string Source { get; }
    internal MacroBlock Body { get; }
    internal Dictionary<string, MacroFunction> Functions { get; }
    internal int NodeCount { get; }
    internal string? SourceDigest { get; private set; }

    public static async Task<MacroProgram> LoadAsync(string path, CancellationToken token = default)
    {
        path = ResolveSourcePath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await LoadAsync(path, stream, token).ConfigureAwait(false);
    }

    internal static async Task<MacroProgram?> TryLoadLocalAsync(string path, CancellationToken token = default)
    {
        path = ResolveSourcePath(path);
        using SafeFileHandle? handle = LocalMacroFile.TryOpen(path);
        if (handle is null) return null;
        await using var stream = new FileStream(handle, FileAccess.Read);
        return await LoadAsync(path, stream, token).ConfigureAwait(false);
    }

    internal static string ResolveSourcePath(string path)
    {
        try
        {
            MacroLimits.Unicode(path);
            return OrdinaryWindowsPath.Resolve(path);
        }
        catch (Exception error) when (error is MacroError or InvalidDataException or ArgumentException or PathTooLongException)
        { throw SourceError("Path"); }
    }

    private static async Task<MacroProgram> LoadAsync(string path, FileStream stream, CancellationToken token)
    {
        long length = stream.Length;
        if (length > MacroLimits.SourceBytes) throw SourceError("SourceLimit");
        DateTime stamp = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        byte[] bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.Length != length || stream.ReadByte() != -1 || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != stamp)
            throw SourceError("SourceChanged");
        Encoding encoding = new UTF8Encoding(false, true);
        int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        try
        {
            var program = Parse(path, encoding.GetString(bytes, skip, bytes.Length - skip));
            program.SourceDigest = Convert.ToHexString(SHA256.HashData(bytes));
            return program;
        }
        catch (DecoderFallbackException) { throw SourceError("SourceEncoding"); }
    }

    public static MacroProgram Parse(string path, string source)
    {
        if (source.Length > MacroLimits.StringUnits) throw SourceError("SourceLimit");
        var budget = new MacroSyntaxBudget();
        var parser = new MacroParser(source, new(1, 1), budget);
        var (body, functions) = parser.ParseFile();
        var result = new MacroProgram(Path.GetFullPath(path), source, body, functions, budget.Nodes);
        new MacroChecker(result).Check();
        return result;
    }

    private static MacroSourceException SourceError(string code) => new(code, new(1, 1));
}

internal sealed class MacroSyntaxBudget
{
    public int Nodes;
    public int Depth;
    public T Add<T>(T node) where T : MacroNode
    {
        if (++Nodes > 100_000) throw new MacroSourceException("SyntaxLimit", node.Position);
        if (node is MacroExpression expression)
        {
            expression.Depth = 1 + (expression switch
            {
                MacroUnary u => u.Value.Depth,
                MacroBinary b => Math.Max(b.Left.Depth, b.Right.Depth),
                MacroCast c => c.Value.Depth,
                MacroMember m => m.Target.Depth,
                MacroIndex i => Math.Max(i.Target.Depth, i.Index.Depth),
                MacroCall c => c.Arguments.Select(a => a.Depth).DefaultIfEmpty(0).Max(),
                MacroListLiteral l => l.Values.Select(a => a.Depth).DefaultIfEmpty(0).Max(),
                MacroInterpolation i => i.Parts.Select(a => a.Depth).DefaultIfEmpty(0).Max(),
                _ => 0,
            });
            if (expression.Depth > 128) throw new MacroSourceException("NestingLimit", node.Position);
        }
        return node;
    }
}

internal sealed record MacroToken(string Kind, string Text, object? Value, MacroPosition Position, int Offset);
internal sealed record MacroStringExpression(string Source, MacroPosition Position);

internal sealed class MacroLexer(string source, MacroPosition origin)
{
    private int offset;
    private int line = origin.Line, column = origin.Column;
    private bool wasCr;
    private int stringDepth;
    public MacroToken Next()
    {
        SkipTrivia();
        int start = offset;
        var at = new MacroPosition(line, column);
        if (offset == source.Length) return new("end", "", null, at, offset);
        char c = source[offset];
        if (c is '"' or '$' or '@')
        {
            bool interpolated = false, verbatim = false;
            for (int count = 0; count < 2 && Peek() is '$' or '@'; count++)
            {
                char prefix = Take();
                if (prefix == '$' && !interpolated) interpolated = true;
                else if (prefix == '@' && !verbatim) verbatim = true;
                else throw Error("StringPrefix", at);
            }
            if (Peek() != '"') throw Error("StringExpected", at);
            return ReadString(at, start, interpolated, verbatim);
        }
        if (char.IsAsciiLetter(c) || c == '_')
        {
            Take();
            while (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_') Take();
            return new("id", source[start..offset], null, at, start);
        }
        if (char.IsAsciiDigit(c))
        {
            Take();
            while (char.IsAsciiDigit(Peek()) || Peek() == '_') Take();
            if (Peek() == '.') { Take(); while (char.IsAsciiDigit(Peek()) || Peek() == '_') Take(); }
            if (Peek() is 'e' or 'E')
            {
                Take(); if (Peek() is '+' or '-') Take();
                while (char.IsAsciiDigit(Peek()) || Peek() == '_') Take();
            }
            if (Peek() is 'l' or 'L' or 'd' or 'D') Take();
            string text = source[start..offset];
            return new("num", text, ParseNumber(text, at), at, start);
        }
        string symbol = Take().ToString();
        if (offset < source.Length && (symbol + Peek()) is "==" or "!=" or "<=" or ">=" or "&&" or "||"
            or "+=" or "-=" or "*=" or "/=" or "%=" or "++" or "--") symbol += Take();
        if (!"#(){}[];,.+-*/%!=<>&|".Contains(symbol[0])) throw Error("UnexpectedCharacter", at);
        return new("symbol", symbol, null, at, start);
    }

    private MacroToken ReadString(MacroPosition at, int start, bool interpolated, bool verbatim)
    {
        if (++stringDepth > 128) throw Error("NestingLimit", at);
        Take();
        var parts = new List<object>();
        var text = new StringBuilder();
        while (offset < source.Length)
        {
            char c = Take();
            if (c == '"')
            {
                if (verbatim && Peek() == '"') { Take(); text.Append('"'); continue; }
                parts.Add(text.ToString());
                stringDepth--;
                return new(interpolated ? "interp" : "str", "", interpolated ? parts : text.ToString(), at, start);
            }
            if (!verbatim && c is '\r' or '\n') throw Error("StringNewline", at);
            if (!verbatim && c == '\\') { AppendEscape(text, at); continue; }
            if (interpolated && c is '{' or '}')
            {
                if (Peek() == c) { Take(); text.Append(c); continue; }
                if (c == '}') throw Error("InterpolationBrace", at);
                parts.Add(text.ToString()); text.Clear();
                int expressionStart = offset;
                var expressionPosition = new MacroPosition(line, column);
                int depth = 1;
                MacroToken token;
                do
                {
                    token = Next();
                    if (token.Kind == "end") throw Error("UnterminatedInterpolation", at);
                    if (token.Text == "{") depth++;
                    if (token.Text == "}") depth--;
                    if (depth > 128) throw Error("NestingLimit", at);
                } while (depth != 0);
                parts.Add(new MacroStringExpression(source[expressionStart..token.Offset], expressionPosition));
            }
            else text.Append(c);
        }
        throw Error("UnterminatedString", at);
    }

    private void AppendEscape(StringBuilder target, MacroPosition at)
    {
        char c = Take();
        if (c is 'x' or 'u' or 'U')
        {
            int min = c == 'x' ? 1 : c == 'u' ? 4 : 8, max = c == 'U' ? 8 : 4;
            uint value = 0;
            int count = 0;
            while (count < max && char.IsAsciiHexDigit(Peek()))
            {
                value = (value << 4) | (uint)int.Parse(Take().ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                count++;
            }
            if (count < min || c == 'U' && (value > 0x10ffff || value is >= 0xd800 and <= 0xdfff))
                throw Error("StringEscape", at);
            if (c == 'U') target.Append(char.ConvertFromUtf32((int)value));
            else target.Append((char)value);
            return;
        }
        target.Append(c switch
        {
            '0' => '\0', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r',
            't' => '\t', 'v' => '\v', '\\' => '\\', '\'' => '\'', '"' => '"',
            _ => throw Error("StringEscape", at),
        });
    }

    private static object ParseNumber(string text, MacroPosition at)
    {
        const string digits = "[0-9](?:_?[0-9])*";
        bool floating = text.Contains('.') || text.IndexOfAny(['e', 'E', 'd', 'D']) >= 0;
        string pattern = floating ? $"^{digits}(?:\\.{digits})?(?:[eE][+-]?{digits})?[dD]?$" : $"^{digits}[lL]?$";
        if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw Error("NumericLiteral", at);
        string plain = text.Replace("_", "").TrimEnd('l', 'L', 'd', 'D');
        if (floating && double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) return d;
        if (!floating && long.TryParse(plain, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return n;
        if (!floating && plain == "9223372036854775808") return 9223372036854775808UL;
        throw Error("NumericLiteral", at);
    }

    private void SkipTrivia()
    {
        while (offset < source.Length)
        {
            if (char.IsWhiteSpace(Peek())) { Take(); continue; }
            if (Peek() == '/' && Peek(1) == '/')
            { while (offset < source.Length && Peek() is not '\r' and not '\n') Take(); continue; }
            if (Peek() == '/' && Peek(1) == '*')
            {
                var at = new MacroPosition(line, column); Take(); Take();
                while (offset < source.Length && !(Peek() == '*' && Peek(1) == '/')) Take();
                if (offset == source.Length) throw Error("UnterminatedComment", at);
                Take(); Take(); continue;
            }
            break;
        }
    }
    private char Peek(int ahead = 0) => offset + ahead < source.Length ? source[offset + ahead] : '\0';
    private char Take()
    {
        if (offset >= source.Length) return '\0';
        char c = source[offset++];
        if (c == '\r') { line++; column = 1; }
        else if (c == '\n') { if (!wasCr) line++; column = 1; }
        else column++;
        wasCr = c == '\r';
        return c;
    }
    private static MacroSourceException Error(string code, MacroPosition position) => new(code, position);
}

internal sealed class MacroParser
{
    private readonly List<MacroToken> tokens = [];
    private readonly MacroSyntaxBudget budget;
    private readonly string source;
    private int cursor;
    public MacroParser(string source, MacroPosition origin, MacroSyntaxBudget budget)
    {
        this.budget = budget;
        this.source = source;
        var lexer = new MacroLexer(source, origin);
        MacroToken token;
        do
        {
            token = lexer.Next(); tokens.Add(token);
            if (tokens.Count > 500_000) throw new MacroSourceException("SyntaxLimit", token.Position);
        } while (token.Kind != "end");
    }

    public (MacroBlock Body, Dictionary<string, MacroFunction> Functions) ParseFile()
    {
        int directiveStart = Current.Offset;
        const string header = "#tttym 1";
        int headerEnd = directiveStart + header.Length;
        if (!source.AsSpan(directiveStart).StartsWith(header, StringComparison.Ordinal) ||
            headerEnd < source.Length && source[headerEnd] is not ('\r' or '\n'))
            Fail("VersionLine");
        Expect("#"); Expect("tttym");
        MacroToken version = Current;
        Expect("1");
        if (Current.Kind != "end" && Current.Position.Line == version.Position.Line) Fail("VersionLine");
        var statements = new List<MacroStatement>();
        var functions = new Dictionary<string, MacroFunction>(StringComparer.Ordinal);
        while (Current.Kind != "end")
        {
            int save = cursor;
            if (IsType(Current.Text) || Current.Text == "void")
            {
                MacroType type = ParseType(allowVoid: true);
                string name = Identifier();
                if (Eat("("))
                {
                    var args = new List<MacroParameter>();
                    if (!Eat(")"))
                    {
                        do
                        {
                            MacroType argType = ParseType();
                            args.Add(new(Identifier(), argType));
                            if (args.Count > 32) Fail("ParameterLimit");
                        } while (Eat(","));
                        Expect(")");
                    }
                    var function = new MacroFunction(name, type, args.ToArray(), Block(), tokens[save].Position);
                    if (!functions.TryAdd(name, function)) Fail("DuplicateFunction");
                    if (functions.Count > 256) Fail("FunctionLimit");
                    continue;
                }
            }
            cursor = save;
            statements.Add(Statement());
        }
        return (Add(new MacroBlock(statements.ToArray(), new(1, 1))), functions);
    }

    private MacroStatement Statement()
    {
        Enter();
        try
        {
            var at = Current.Position;
            if (Current.Text == "{") return Block();
            if (Eat("if"))
            {
                Expect("("); var condition = Expression(); Expect(")"); var yes = Block();
                MacroStatement? no = null;
                if (Eat("else")) no = Current.Text == "if" ? Statement() : Block();
                return Add(new MacroIf(condition, yes, no, at));
            }
            if (Eat("while"))
            { Expect("("); var condition = Expression(); Expect(")"); return Add(new MacroWhile(condition, Block(), at)); }
            if (Eat("for"))
            {
                Expect("("); var initial = Current.Text == ";" ? null : Simple(); Expect(";");
                var condition = Current.Text == ";" ? null : Expression(); Expect(";");
                var step = Current.Text == ")" ? null : Simple(allowDeclaration: false); Expect(")");
                return Add(new MacroFor(initial, condition, step, Block(), at));
            }
            if (Eat("foreach"))
            {
                Expect("("); MacroType? type = Eat("var") ? null : ParseType();
                string name = Identifier(); Expect("in"); var values = Expression(); Expect(")");
                return Add(new MacroForeach(name, type, values, Block(), at));
            }
            if (Eat("try"))
            {
                var body = Block(); Expect("catch"); Expect("("); Expect("MacroError");
                string name = Identifier(); Expect(")");
                return Add(new MacroTry(body, name, Block(), at));
            }
            if (Current.Text is "return" or "break" or "continue")
            {
                string kind = Take().Text;
                var value = kind == "return" && Current.Text != ";" ? Expression() : null;
                Expect(";"); return Add(new MacroFlow(kind, value, at));
            }
            var statement = Simple(); Expect(";"); return statement;
        }
        finally { budget.Depth--; }
    }

    private MacroStatement Simple(bool allowDeclaration = true)
    {
        var at = Current.Position;
        if (allowDeclaration && (Current.Text == "var" || IsType(Current.Text)))
        {
            MacroType? type = Eat("var") ? null : ParseType(); string name = Identifier();
            Expect("="); return Add(new MacroDeclaration(name, type, Expression(), at));
        }
        var target = Expression();
        if (Current.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "++" or "--")
        {
            string op = Take().Text;
            return Add(new MacroAssignment(target, op, op is "++" or "--" ? null : Expression(), at));
        }
        if (target is MacroCall call) return Add(new MacroInvocation(call, at));
        Fail("StatementExpected"); return null!;
    }

    private MacroBlock Block()
    {
        var at = Current.Position; Expect("{"); var statements = new List<MacroStatement>();
        while (!Eat("}")) { if (Current.Kind == "end") Fail("BlockExpected"); statements.Add(Statement()); }
        return Add(new MacroBlock(statements.ToArray(), at));
    }

    public MacroExpression ParseFragment()
    {
        var result = Expression(); if (Current.Kind != "end") Fail("ExpressionExpected"); return result;
    }

    private MacroExpression Expression(int minimum = 1)
    {
        Enter();
        try
        {
            MacroExpression left = Prefix();
            while (Precedence(Current.Text) >= minimum)
            {
                MacroToken op = Take();
                left = Add(new MacroBinary(op.Text, left, Expression(Precedence(op.Text) + 1), op.Position));
            }
            return left;
        }
        finally { budget.Depth--; }
    }

    private MacroExpression Prefix()
    {
        var token = Take(); MacroExpression expression;
        if (token.Text is "!" or "+" or "-")
        {
            if (token.Text == "-" && Current.Value is ulong)
            { Take(); return Add(new MacroLiteral(long.MinValue, token.Position)); }
            return Add(new MacroUnary(token.Text, Expression(7), token.Position));
        }
        if (token.Text == "(")
        {
            if (Current.Text is "long" or "double" && tokens[cursor + 1].Text == ")")
            {
                var type = ParseType(); Expect(")"); return Add(new MacroCast(type, Expression(7), token.Position));
            }
            expression = Expression(); Expect(")");
        }
        else if (token.Text == "[")
        {
            var values = new List<MacroExpression>();
            if (!Eat("]"))
            {
                do { values.Add(Expression()); } while (Eat(",") && Current.Text != "]");
                Expect("]");
            }
            expression = Add(new MacroListLiteral(values.ToArray(), token.Position));
        }
        else if (token.Kind is "str" or "num")
        {
            if (token.Value is ulong) throw new MacroSourceException("NumericLiteral", token.Position);
            expression = Add(new MacroLiteral(token.Value!, token.Position));
        }
        else if (token.Kind == "interp")
        {
            var parts = ((List<object>)token.Value!).Select(part => part is MacroStringExpression fragment
                ? new MacroParser(fragment.Source, fragment.Position, budget).ParseFragment()
                : Add<MacroExpression>(new MacroLiteral((string)part, token.Position))).ToArray();
            expression = Add(new MacroInterpolation(parts, token.Position));
        }
        else if (token.Text is "true" or "false") expression = Add(new MacroLiteral(token.Text == "true", token.Position));
        else if (token.Kind == "id")
        {
            if (Eat("("))
            {
                var args = new List<MacroExpression>();
                if (!Eat(")"))
                { do { args.Add(Expression()); if (args.Count > 32) Fail("ParameterLimit"); } while (Eat(",")); Expect(")"); }
                expression = Add(new MacroCall(token.Text, args.ToArray(), token.Position));
            }
            else expression = Add(new MacroName(token.Text, token.Position));
        }
        else { throw new MacroSourceException("ExpressionExpected", token.Position); }
        while (true)
        {
            if (Eat(".")) expression = Add(new MacroMember(expression, Identifier(), token.Position));
            else if (Eat("[")) { var index = Expression(); Expect("]"); expression = Add(new MacroIndex(expression, index, token.Position)); }
            else break;
        }
        return expression;
    }

    private MacroType ParseType(bool allowVoid = false, int depth = 0)
    {
        MacroToken token = Take();
        if (token.Text == "List")
        {
            if (depth >= 4) Fail("TypeNestingLimit");
            Expect("<"); var element = ParseType(depth: depth + 1); Expect(">"); return MacroType.List(element);
        }
        if (MacroType.ValueNames.Contains(token.Text) || allowVoid && token.Text == "void") return new(token.Text);
        throw new MacroSourceException("TypeExpected", token.Position);
    }
    private static bool IsType(string name) => name == "List" || MacroType.ValueNames.Contains(name);
    private static int Precedence(string op) => op switch
    { "||" => 1, "&&" => 2, "==" or "!=" => 3, "<" or "<=" or ">" or ">=" => 4, "+" or "-" => 5, "*" or "/" or "%" => 6, _ => 0 };
    private T Add<T>(T node) where T : MacroNode => budget.Add(node);
    private MacroToken Current => tokens[Math.Min(cursor, tokens.Count - 1)];
    private MacroToken Take() { var token = Current; if (cursor < tokens.Count - 1) cursor++; return token; }
    private bool Eat(string text) { if (Current.Text != text) return false; Take(); return true; }
    private void Expect(string text) { if (!Eat(text)) Fail("ExpectedToken"); }
    private string Identifier() { if (Current.Kind != "id") Fail("IdentifierExpected"); return Take().Text; }
    private void Fail(string code) => throw new MacroSourceException(code, Current.Position);
    private void Enter() { if (++budget.Depth > 128) Fail("NestingLimit"); }
}
