namespace TabTty.Core;

internal sealed record MacroConstantsDeclaration(string Alias, string Path, MacroPosition AliasPosition, MacroPosition Position)
{
    internal string Resolve(string macroPath)
    {
        try
        {
            MacroLimits.Unicode(Path);
            return OrdinaryWindowsPath.Resolve(Path, System.IO.Path.GetDirectoryName(macroPath)!);
        }
        catch (Exception error) when (error is MacroError or InvalidDataException or ArgumentException or PathTooLongException)
        { throw new MacroSourceException("Path", Position, macroPath); }
    }
}
internal sealed record MacroConstantsBinding(string Alias, MacroConstantsFile File);
internal sealed record MacroConstantsCapture(string Text, string Digest, int? ByteCount);

internal static class MacroConstantsBinder
{
    internal static async ValueTask<IReadOnlyList<MacroConstantsBinding>?> BindAsync(string macroPath,
        IReadOnlyList<MacroConstantsDeclaration> declarations,
        Func<string, int, ValueTask<MacroConstantsCapture?>> capture, CancellationToken token = default,
        Action<MacroConstantsDeclaration, string?>? dependency = null, Action<MacroSourceException>? report = null,
        Func<MacroConstantsCapture, IDisposable>? reserveParse = null, Action? admitMember = null,
        Action<string, MacroToken, object>? definition = null, Action<MacroConstantsFile>? admitted = null)
    {
        var budget = new MacroConstantsBudget();
        var paths = new Dictionary<string, (MacroConstantsFile? File, string? Error)>(StringComparer.Ordinal);
        var bindings = new List<MacroConstantsBinding>(declarations.Count);
        foreach (var declaration in declarations)
        {
            token.ThrowIfCancellationRequested();
            string path;
            try { path = declaration.Resolve(macroPath); }
            catch (MacroSourceException error) when (report is not null)
            {
                dependency?.Invoke(declaration, null);
                report(error);
                continue;
            }
            dependency?.Invoke(declaration, path);
            if (!paths.TryGetValue(path, out var entry))
            {
                try
                {
                    var captured = await capture(path, budget.RemainingBytes).ConfigureAwait(false);
                    if (captured is null) return null;
                    if (budget.CrossedByteLimit(captured.ByteCount))
                    {
                        var excess = new MacroSourceException("ConstantsLimit", declaration.Position, macroPath);
                        if (report is null) throw excess;
                        report(excess);
                    }
                    using var scratch = reserveParse?.Invoke(captured);
                    var file = MacroConstantsFile.Parse(path, captured.Digest, captured.Text, budget.RemainingMembers,
                        admitMember ?? (static () => { }), definition is null ? null : (name, value) => definition(path, name, value));
                    budget.AddDefinitions(file);
                    admitted?.Invoke(file);
                    entry = (file, null);
                }
                catch (MacroSourceException error) when (report is not null)
                {
                    entry = (null, error.Code is "ConstantsLimit" or "ConstantsRead" ? error.Code : "ConstantsInvalid");
                }
                paths.Add(path, entry);
            }
            if (entry.File is { } valid) bindings.Add(new(declaration.Alias, valid));
            else report!(new(entry.Error!, declaration.Position, macroPath));
        }
        return bindings.AsReadOnly();
    }
}

internal sealed class MacroConstantsBudget
{
    private long knownBytes;
    internal int RemainingBytes => (int)Math.Max(0, MacroLimits.SourceBytes - knownBytes);
    internal int RemainingMembers { get; private set; } = MacroConstantsFile.MaxMembers;

    internal bool CrossedByteLimit(int? byteCount)
    {
        if (byteCount is not >= 0) return false;
        bool wasWithinLimit = knownBytes <= MacroLimits.SourceBytes;
        knownBytes += byteCount.Value;
        return wasWithinLimit && knownBytes > MacroLimits.SourceBytes;
    }

    internal void AddDefinitions(MacroConstantsFile file) => RemainingMembers -= file.Values.Count;
}

internal sealed record MacroConstantsFile(string FullPath, string Digest, string Source, IReadOnlyDictionary<string, object> Values)
{
    internal const int MaxDeclarations = 16;
    internal const int MaxMembers = 4096;

    internal static MacroConstantsFile Parse(string fullPath, string digest, string source, int remainingMembers,
        Action admitMember, Action<MacroToken, object>? onDefinition = null)
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
                onDefinition?.Invoke(name, values[name.Text]);
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

        MacroToken number = initializer.Text is "+" or "-" ? lexer.Next() : initializer;
        if (number.Kind != "num") throw new MacroSourceException("ConstantsLiteral", number.Position);
        return number.LiteralValue(negative: initializer.Text == "-");
    }
}
