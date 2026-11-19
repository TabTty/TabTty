namespace TabTty.Core;

internal sealed record MacroConstantsDeclaration(string Alias, string Path, MacroPosition Position);
internal sealed record MacroConstantsBinding(string Alias, MacroConstantsFile File);

internal sealed record MacroConstantsFile(string FullPath, string Digest, string Source, IReadOnlyDictionary<string, object> Values)
{
    internal const int MaxDeclarations = 16;
    internal const int MaxMembers = 4096;

    internal static MacroConstantsFile Parse(string fullPath, string digest, string source, int remainingMembers,
        Action admitMember)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var lexer = new MacroLexer(source, new(1, 1), allowInterpolation: false);
        try
        {
            for (MacroToken type = lexer.Next(); type.Kind != "end"; type = lexer.Next())
            {
                if (type.Text is not ("var" or "bool" or "long" or "double" or "string"))
                    throw new MacroSourceException("ConstantsDefinition", type.Position);
                if (values.Count >= remainingMembers)
                    throw new MacroSourceException("ConstantsLimit", type.Position);
                admitMember();

                MacroToken name = lexer.Next();
                if (name.Kind != "id" || MacroChecker.IsReserved(name.Text) || values.ContainsKey(name.Text))
                    throw new MacroSourceException("ConstantsName", name.Position);
                MacroToken assignment = lexer.Next();
                if (assignment.Text != "=") throw new MacroSourceException("ConstantsDefinition", assignment.Position);

                MacroToken initializer = lexer.Next();
                object value = ReadLiteral(initializer, lexer);
                MacroToken end = lexer.Next();
                if (end.Text != ";")
                    throw new MacroSourceException(end.Kind == "end" ? "ConstantsDefinition" : "ConstantsLiteral", end.Position);

                values.Add(name.Text, type.Text switch
                {
                    "var" => value,
                    "bool" when value is bool => value,
                    "long" when value is long => value,
                    "double" when value is double => value,
                    "double" when value is long integer => (double)integer,
                    "string" when value is string => value,
                    _ => throw new MacroSourceException("ConstantsType", initializer.Position),
                });
            }
        }
        catch (MacroSourceException error)
        {
            throw new MacroSourceException(error.Code, error.Position, fullPath);
        }
        return new(fullPath, digest, source, values.AsReadOnly());
    }

    private static object ReadLiteral(MacroToken initializer, MacroLexer lexer)
    {
        if (initializer.Kind == "str") return initializer.Value!;
        if (initializer.Text is "true" or "false") return initializer.Text == "true";

        bool negative = initializer.Text == "-";
        MacroToken number = initializer.Text is "+" or "-" ? lexer.Next() : initializer;
        if (number.Kind != "num") throw new MacroSourceException("ConstantsLiteral", number.Position);
        return number.Value switch
        {
            long integer => (object)(negative ? -integer : integer),
            double real => negative ? -real : real,
            ulong when negative => long.MinValue,
            _ => throw new MacroSourceException("NumericLiteral", number.Position),
        };
    }
}
