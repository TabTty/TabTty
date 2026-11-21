using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TabTty.Core;

internal sealed class MacroSyntaxBudget(ResourceReservation? tokens = null, ResourceReservation? retained = null)
{
    public int Nodes;
    public int Depth;
    private int tokenCount;
    public void Token(MacroPosition position)
    {
        if (++tokenCount > 500_000) throw new MacroSourceException("SyntaxLimit", position);
        tokens?.Add(96);
    }
    public T Add<T>(T node) where T : MacroNode
    {
        retained?.Add(64);
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
                MacroAlternatives a => a.Patterns.Max(p => p.Depth),
                MacroListLiteral l => l.Values.Select(a => a.Depth).DefaultIfEmpty(0).Max(),
                MacroInterpolation i => i.Parts.Select(a => a.Depth).DefaultIfEmpty(0).Max(),
                _ => 0,
            });
            if (expression.Depth > 128) throw new MacroSourceException("NestingLimit", node.Position);
        }
        return node;
    }
}

internal sealed record MacroToken(string Kind, string Text, object? Value, MacroPosition Position, int Offset, int EndOffset = -1);

internal sealed class MacroLexer(string source, MacroPosition origin, bool allowInterpolation = true,
    MacroEditorSymbols? editor = null, MacroSyntaxBudget? budget = null)
{
    private int offset;
    private int line = origin.Line, column = origin.Column;
    private bool wasCr;
    private int stringDepth;
    internal int Offset => offset;
    internal int FirstTokenOffset { get; private set; } = -1;
    public MacroToken Next()
    {
        SkipTrivia();
        if (FirstTokenOffset < 0) FirstTokenOffset = offset;
        int start = offset;
        var at = new MacroPosition(line, column);
        budget?.Token(at);
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
            if (interpolated && !allowInterpolation) throw Error("ConstantsLiteral", at);
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
        if (!"#(){}[];,:.+-*/%!=<>&|".Contains(symbol[0])) throw Error("UnexpectedCharacter", at);
        return new("symbol", symbol, null, at, start);
    }

    private MacroToken ReadString(MacroPosition at, int start, bool interpolated, bool verbatim)
    {
        if (++stringDepth > 128) throw Error("NestingLimit", at);
        try { return ReadStringParts(at, start, interpolated, verbatim); }
        finally { stringDepth--; }
    }

    private MacroToken ReadStringParts(MacroPosition at, int start, bool interpolated, bool verbatim)
    {
        Take();
        var parts = new List<object>();
        var text = new StringBuilder();
        while (offset < source.Length)
        {
            char c = Take();
            if (c == '"')
            {
                if (verbatim && Peek() == '"') { Take(); text.Append('"'); continue; }
                return Finish();
            }
            if (!verbatim && c is '\r' or '\n') throw Error("StringNewline", at);
            if (!verbatim && c == '\\') { AppendEscape(text, new(line, column - 1)); continue; }
            if (interpolated && c is '{' or '}')
            {
                if (Peek() == c) { Take(); text.Append(c); continue; }
                if (c == '}') throw Error("InterpolationBrace", at);
                parts.Add(text.ToString()); text.Clear();
                var fragment = new List<MacroToken>();
                int depth = 1;
                MacroToken token;
                do
                {
                    try { token = Next(); }
                    catch (MacroSourceException error) when (offset == source.Length && editor?.Recover(error) == true)
                    { token = new("end", "", null, new(line, column), offset); }
                    if (token.Kind == "end")
                    {
                        var error = Error("UnterminatedInterpolation", at);
                        if (editor?.Recover(error) != true) throw error;
                        break;
                    }
                    if (token.Text == "{") depth++;
                    if (token.Text == "}") depth--;
                    if (depth > 128) throw Error("NestingLimit", at);
                    if (depth != 0) fragment.Add(token);
                } while (depth != 0);
                fragment.Add(new("end", "", null, token.Position, token.Offset));
                parts.Add(fragment);
            }
            else text.Append(c);
        }
        var unfinished = Error("UnterminatedString", at);
        if (!interpolated || editor?.Recover(unfinished) != true) throw unfinished;
        return Finish();

        MacroToken Finish()
        {
            if (!interpolated) return new("str", "", text.ToString(), at, start, offset);
            parts.Add(text.ToString());
            return new("interp", "", parts, at, start, offset);
        }
    }

    private void AppendEscape(StringBuilder target, MacroPosition at)
    {
        char c = Take();
        if (c is 'x' or 'u' or 'U')
        {
            int digits = c == 'x' ? 2 : c == 'u' ? 4 : 8;
            uint value = 0;
            int count = 0;
            while (count < digits && char.IsAsciiHexDigit(Peek()))
            {
                value = (value << 4) | (uint)int.Parse(Take().ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                count++;
            }
            if (count != digits || c == 'U' && (value > 0x10ffff || value is >= 0xd800 and <= 0xdfff))
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
        if (!floating && plain.AsSpan().TrimStart('0').SequenceEqual("9223372036854775808")) return 9223372036854775808UL;
        throw Error("NumericLiteral", at);
    }

    private void SkipTrivia()
    {
        while (offset < source.Length)
        {
            if (char.IsWhiteSpace(Peek())) { Take(); continue; }
            if (Peek() == '/' && Peek(1) == '/')
            {
                int start = offset;
                while (offset < source.Length && Peek() is not '\r' and not '\n') Take();
                editor?.Comment(start, offset, includeEnd: true);
                continue;
            }
            if (Peek() == '/' && Peek(1) == '*')
            {
                int start = offset;
                var at = new MacroPosition(line, column); Take(); Take();
                while (offset < source.Length && !(Peek() == '*' && Peek(1) == '/')) Take();
                bool closed = offset != source.Length;
                if (closed) { Take(); Take(); }
                editor?.Comment(start, offset, includeEnd: !closed);
                if (!closed) throw Error("UnterminatedComment", at);
                continue;
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
    private readonly List<MacroToken> tokens;
    private readonly MacroSyntaxBudget budget;
    private readonly string source;
    private readonly MacroEditorSymbols? editor;
    private int cursor;
    private readonly List<MacroConstantsDeclaration> constantsDeclarations = [];
    internal IReadOnlyList<MacroConstantsDeclaration> ConstantsDeclarations => constantsDeclarations;
    public MacroParser(string source, MacroPosition origin, MacroSyntaxBudget budget, MacroEditorSymbols? editor = null)
    {
        this.budget = budget;
        this.source = source;
        this.editor = editor;
        tokens = [];
        var lexer = new MacroLexer(source, origin, editor: editor, budget: budget);
        MacroToken token;
        do
        {
            int start = lexer.Offset;
            try { token = lexer.Next(); }
            catch (MacroSourceException error) when (editor?.Recover(error) == true)
            {
                // A bad token does not hide later directives or calls from the editor.
                token = start < lexer.Offset && lexer.Offset < source.Length
                    ? new("invalid", "", null, error.Position, start, lexer.Offset)
                    : new("end", "", null, error.Position, source.Length);
            }
            tokens.Add(token);
            if (tokens.Count == 3) editor?.CheckVersion(tokens);
        } while (token.Kind != "end");
        editor?.VersionHeader(tokens, lexer.FirstTokenOffset, source.Length);
    }

    // Fragment tokens already carry positions and offsets in the original document.
    private MacroParser(MacroParser parent, List<MacroToken> fragment)
    {
        source = parent.source;
        budget = parent.budget;
        editor = parent.editor;
        tokens = fragment;
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
        while (Current.Text == "#")
        {
            try { ParseConstantsDeclaration(); }
            catch (MacroSourceException error) when (editor?.Recover(error) == true) { break; }
        }
        var statements = new List<MacroStatement>();
        var functions = new Dictionary<string, MacroFunction>(StringComparer.Ordinal);
        while (Current.Kind != "end")
        {
            int start = cursor;
            try
            {
                if (!TryFunction(functions)) statements.Add(Statement());
            }
            catch (MacroSourceException error) when (editor?.Recover(error) == true)
            {
                RecoverStatement(start, inBlock: false);
            }
        }
        return (Add(new MacroBlock(statements.ToArray(), new(1, 1))), functions);
    }

    private bool TryFunction(Dictionary<string, MacroFunction> functions)
    {
        if (!IsType(Current.Text) && Current.Text != "void") return false;
        int start = cursor;
        MacroType type = ParseType(allowVoid: true);
        var name = Current;
        string identifier = Identifier();
        if (!Eat("(")) { cursor = start; return false; }
        var parameters = new List<MacroParameter>();
        if (!Eat(")"))
        {
            do
            {
                MacroType argType = ParseType();
                var argName = Current;
                var parameter = new MacroParameter(Identifier(), argType);
                parameters.Add(parameter);
                editor?.Name(parameter, argName);
                if (parameters.Count > 32) Fail("ParameterLimit");
            } while (Eat(","));
            Expect(")");
        }
        var function = new MacroFunction(identifier, type, parameters.ToArray(), Block(), tokens[start].Position);
        if (!functions.TryAdd(identifier, function)) Fail("DuplicateFunction");
        if (functions.Count > 256) Fail("FunctionLimit");
        editor?.Name(function, name);
        return true;
    }

    private void ParseConstantsDeclaration()
    {
        MacroToken start = Take();
        MacroToken keyword = Take();
        if (keyword.Text != "constants" || keyword.Offset != start.Offset + 1)
            throw new MacroSourceException("ConstantsDeclaration", start.Position);
        MacroToken alias = Current;
        string name = Identifier();
        MacroToken literal = Current;
        if (literal.Kind != "str" || !HorizontalSpace(keyword.Offset + keyword.Text.Length, alias.Offset) ||
            !HorizontalSpace(alias.Offset + name.Length, literal.Offset))
            throw new MacroSourceException("ConstantsDeclaration", literal.Position);
        Take();
        if (source.AsSpan(literal.Offset, literal.EndOffset - literal.Offset).ContainsAny('\r', '\n'))
            throw new MacroSourceException("ConstantsDeclaration", literal.Position);
        var trailing = source.AsSpan(literal.EndOffset).TrimStart(" \t");
        if (!trailing.IsEmpty && trailing[0] is not ('\r' or '\n') && !trailing.StartsWith("//"))
            throw new MacroSourceException("ConstantsDeclaration", literal.Position);
        if (MacroChecker.IsReserved(name) || constantsDeclarations.Any(entry => entry.Alias == name))
            throw new MacroSourceException("ConstantsAlias", alias.Position);
        if (constantsDeclarations.Count >= MacroConstantsFile.MaxDeclarations)
            throw new MacroSourceException("ConstantsLimit", start.Position);
        constantsDeclarations.Add(new(name, (string)literal.Value!, alias.Position, literal.Position));
    }

    private bool HorizontalSpace(int start, int end) =>
        end > start && source.AsSpan(start, end - start).IndexOfAnyExcept(' ', '\t') < 0;

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
                var nameToken = Current;
                string name = Identifier(); Expect("in"); var values = Expression(); Expect(")");
                var loop = Add(new MacroForeach(name, type, values, Block(), at));
                editor?.Name(loop, nameToken);
                return loop;
            }
            if (Eat("try"))
            {
                var body = Block(); Expect("catch"); Expect("("); Expect("MacroError");
                var nameToken = Current;
                string name = Identifier(); Expect(")");
                var attempt = Add(new MacroTry(body, name, Block(), at));
                editor?.Name(attempt, nameToken);
                return attempt;
            }
            if (Current.Text is "return" or "break" or "continue" or "throw")
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
            MacroType? type = Eat("var") ? null : ParseType();
            var nameToken = Current;
            string name = Identifier();
            Expect("=");
            int start = cursor;
            MacroExpression value;
            try
            {
                value = Expression();
                if (editor is not null && Current.Text != ";") Fail("ExpectedToken");
            }
            catch (MacroSourceException error) when (editor?.Recover(error) == true)
            {
                cursor = start;
                while (Current.Kind != "end" && Current.Text is not (";" or "}")) Take();
                value = Add(new MacroMissingExpression(error.Position));
            }
            var declaration = Add(new MacroDeclaration(name, type, value, at));
            editor?.Name(declaration, nameToken, Current.Offset + 1);
            return declaration;
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
        var open = Current; Expect("{"); var statements = new List<MacroStatement>();
        while (Current.Text != "}" && Current.Kind != "end")
        {
            int start = cursor;
            try { statements.Add(Statement()); }
            catch (MacroSourceException error) when (editor?.Recover(error) == true)
            { RecoverStatement(start, inBlock: true); }
        }
        int end = Current.Offset + 1;
        if (!Eat("}"))
        {
            var error = new MacroSourceException("BlockExpected", Current.Position);
            if (editor?.Recover(error) != true) throw error;
        }
        var block = Add(new MacroBlock(statements.ToArray(), open.Position));
        editor?.Block(block, open.Offset + 1, end);
        return block;
    }

    private void RecoverStatement(int start, bool inBlock)
    {
        // Re-scan from the statement start: an expression may already have consumed its delimiter.
        cursor = start;
        int braces = 0;
        while (Current.Kind != "end")
        {
            if (Current.Text == "}" && braces == 0)
            {
                if (!inBlock) Take();
                return;
            }
            string token = Take().Text;
            if (token == "{") braces++;
            if (token == "}" && --braces == 0) return;
            if (braces == 0 && token == ";") return;
        }
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
            var parts = ((List<object>)token.Value!).Select(part => part is List<MacroToken> fragment
                ? ParseInterpolation(fragment)
                : Add<MacroExpression>(new MacroLiteral((string)part, token.Position))).ToArray();
            expression = Add(new MacroInterpolation(parts, token.Position));
        }
        else if (token.Text is "true" or "false") expression = Add(new MacroLiteral(token.Text == "true", token.Position));
        else if (token.Kind == "id")
        {
            if (Current.Text == "(") expression = ParseCall(token);
            else
            {
                editor?.ReferenceName(token);
                expression = Add(new MacroName(token.Text, token.Position));
            }
        }
        else { throw new MacroSourceException("ExpressionExpected", token.Position); }
        while (true)
        {
            if (Current.Text == ".")
            {
                var dot = Take();
                editor?.QualifiedMember(expression, dot, Current);
                var member = Current;
                expression = Add(new MacroMember(expression, Identifier(), member.Position));
            }
            else if (Current.Text == "[")
            {
                var bracket = Take();
                var index = Expression();
                Expect("]");
                expression = Add(new MacroIndex(expression, index, bracket.Position));
            }
            else break;
        }
        return expression;
    }

    private MacroExpression ParseInterpolation(List<MacroToken> fragment)
    {
        try { return new MacroParser(this, fragment).ParseFragment(); }
        catch (MacroSourceException error) when (editor?.Recover(error) == true)
        { return Add(new MacroMissingExpression(error.Position)); }
    }

    private MacroCall ParseCall(MacroToken name)
    {
        int open = Take().Offset;
        int argumentStart = open + 1;
        var args = new List<MacroExpression>();
        var parameterSlots = new List<MacroArgumentSlot>();
        (int Start, int End)? patternRange = null;
        while (true)
        {
            int index = args.Count;
            if (Current.Text == ")" && index == 0)
            {
                editor?.CallArgument(name.Text, open, index, argumentStart, Current.Offset, parameterSlots);
                break;
            }
            int first = Current.Offset;
            int optionNameEnd = index > 0 && MacroApi.NamedParameters.ContainsKey(name.Text)
                ? first + (Current.Kind == "id" ? Current.Text.Length : 0) : -1;
            bool hasOptionColon = Current.Kind == "id" && tokens[cursor + 1].Text == ":";
            try { args.Add(ParseCallArgument(name, index, parameterSlots)); }
            finally
            {
                editor?.CallArgument(name.Text, open, index, argumentStart, Current.Offset, parameterSlots,
                    optionNameEnd, hasOptionColon);
            }
            if (args.Count > 32) Fail("ParameterLimit");
            if (editor is not null && index == 0 && name.Text == "WaitPrompt")
            {
                var last = tokens[cursor - 1];
                int end = last.EndOffset >= 0 ? last.EndOffset : last.Offset + last.Text.Length;
                patternRange = (first, end);
            }
            if (Current.Text != ",") break;
            argumentStart = Take().Offset + 1;
        }
        Expect(")");
        var call = Add(new MacroCall(name.Text, args.ToArray(), name.Position)
            { ParameterSlots = parameterSlots.Count == 0 ? null : parameterSlots.ToArray() });
        editor?.CallOptions(open, parameterSlots);
        if (patternRange.HasValue) editor!.WaitPatternArgument(call, patternRange.Value);
        return call;
    }

    private MacroExpression ParseCallArgument(MacroToken name, int index, List<MacroArgumentSlot> parameterSlots)
    {
        if (index == 0 && name.Text is "WaitAnyLine" or "WaitAnyPrompt")
            return ParseAlternatives();
        if (!MacroApi.NamedParameters.TryGetValue(name.Text, out var parameters))
            return Expression();
        if (index == 0) parameterSlots.Add(new(0, Current.Position));
        else
        {
            var option = Current;
            if (option.Kind != "id") Fail("NamedOptionExpected");
            Take();
            int slot = Array.IndexOf(parameters, option.Text, 1);
            if (slot < 0) throw new MacroSourceException("UnknownOption", option.Position, length: option.Text.Length);
            if (parameterSlots.Any(parameter => parameter.Index == slot))
                throw new MacroSourceException("DuplicateOption", option.Position, length: option.Text.Length);
            Expect(":");
            parameterSlots.Add(new(slot, Current.Position));
        }
        return Expression();
    }

    private MacroAlternatives ParseAlternatives()
    {
        var opening = Current;
        Expect("[");
        var names = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var patterns = new List<MacroExpression>();
        do
        {
            var label = Current;
            if (label.Kind != "id" || MacroChecker.IsReserved(label.Text) ||
                tokens[cursor + 1].Text != ":")
                throw new MacroSourceException("AlternativeNameExpected", label.Position, length: Math.Max(1, label.Text.Length));
            if (!unique.Add(label.Text))
                throw new MacroSourceException("DuplicateAlternative", label.Position, length: label.Text.Length);
            Take(); Take();
            names.Add(label.Text);
            patterns.Add(Expression());
        } while (Eat(",") && Current.Text != "]");
        Expect("]");
        return Add(new MacroAlternatives(names.ToArray(), patterns.ToArray(), opening.Position));
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
