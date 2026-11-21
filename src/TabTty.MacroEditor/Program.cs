using System.Text.Json;
using TabTty.Core;

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
while (Console.ReadLine() is { } line)
{
    try
    {
        var request = JsonSerializer.Deserialize<EditorRequest>(line, jsonOptions)
            ?? throw new InvalidDataException("Missing request");
        object result = request.Operation == "readLocal" ? ReadLocal(request.Path) : Analyze(request);
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
    catch (Exception error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { error = error.Message }));
    }
}

static object ReadLocal(string path)
{
    try
    {
        using var handle = LocalMacroFile.TryOpen(path);
        if (handle is null) return new { status = "openRequired" };
        long length = RandomAccess.GetLength(handle);
        if (length > MacroLimits.SourceBytes) return new { status = "tooLarge" };
        var bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        var (encoding, skip) = MacroProgram.DetectEncoding(bytes);
        return new { status = "ok", text = encoding.GetString(bytes, skip, bytes.Length - skip) };
    }
    catch (FileNotFoundException) { return new { status = "missing" }; }
    catch (DirectoryNotFoundException) { return new { status = "missing" }; }
    catch (Exception) { return new { status = "unreadable" }; }
}

static object Analyze(EditorRequest request)
{
    var tokens = new List<object>();
    var diagnostics = new List<object>();
    var members = new Dictionary<string, string>();
    var inferred = new List<object>();
    var lexer = new MacroLexer(request.Source, new(1, 1));
    try
    {
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == "end") break;
            tokens.Add(new { token.Kind, token.Text, token.Value, token.Offset, token.EndOffset,
                token.Position.Line, token.Position.Column });
        }
    }
    catch (MacroSourceException error)
    {
        diagnostics.Add(new { error.Code, error.Position.Line, error.Position.Column });
    }
    if (diagnostics.Count == 0)
    {
        try
        {
            if (request.ConstantsFile)
            {
                var file = MacroConstantsFile.Parse(request.Path, "", request.Source, MacroConstantsFile.MaxMembers, () => { });
                foreach (var (name, value) in file.Values)
                    members[name] = value switch
                    {
                        string => "string",
                        long => "long",
                        double => "double",
                        bool => "bool",
                        _ => "?"
                    };
            }
            else
            {
                using var program = MacroProgram.ParseForEditor(request.Path, request.Source,
                    request.Constants ?? new Dictionary<string, string>());
                Collect(program.Body);
                foreach (var function in program.Functions.Values) Collect(function.Body);
            }
        }
        catch (MacroSourceException error)
        {
            diagnostics.Add(new { error.Code, error.Position.Line, error.Position.Column });
        }
    }
    var functions = MacroApi.Signatures.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.Select(signature => new {
            Result = signature.Result.ToString(),
            Parameters = signature.Parameters.Select(parameter => parameter.ToString()).ToArray()
        }).ToArray());
    return new { tokens, diagnostics, functions, members, inferred };

    void Collect(MacroStatement statement)
    {
        switch (statement)
        {
            case MacroDeclaration declaration:
                inferred.Add(new { declaration.Name, Type = declaration.Type.ToString(),
                    declaration.Position.Line, declaration.Position.Column });
                break;
            case MacroBlock block:
                foreach (var child in block.Statements) Collect(child);
                break;
            case MacroIf branch:
                Collect(branch.Yes);
                if (branch.No is not null) Collect(branch.No);
                break;
            case MacroWhile loop:
                Collect(loop.Body);
                break;
            case MacroFor loop:
                if (loop.Initial is not null) Collect(loop.Initial);
                Collect(loop.Body);
                break;
            case MacroForeach loop:
                inferred.Add(new { loop.Name, Type = loop.Type.ToString(),
                    loop.Position.Line, loop.Position.Column });
                Collect(loop.Body);
                break;
            case MacroTry attempt:
                Collect(attempt.Body);
                Collect(attempt.Catch);
                break;
        }
    }
}

internal sealed record EditorRequest(string Path, string Source, bool ConstantsFile,
    Dictionary<string, string>? Constants, string? Operation);
