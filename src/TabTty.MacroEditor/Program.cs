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
    var editor = new MacroEditorSymbols();
    var symbols = new List<MacroEditorSymbol>();
    var members = new Dictionary<string, string>();
    MacroSourceException? diagnostic = null;
    try
    {
        if (request.ConstantsFile)
        {
            MacroConstantsFile.Parse(request.Path, "", request.Source, MacroConstantsFile.MaxMembers, () => { },
                (name, value) =>
                {
                    string type = MacroChecker.ScalarType(value).ToString();
                    members.Add(name.Text, type);
                    symbols.Add(new(name.Text, type, name.Position));
                });
        }
        else
        {
            symbols = MacroProgram.AnalyzeForEditor(request.Path, request.Source,
                request.Constants ?? new Dictionary<string, string>(), editor, request.Offset);
            diagnostic = editor.Error;
        }
    }
    catch (MacroSourceException error) { diagnostic = error; }
    var diagnostics = diagnostic is null ? [] : new[] {
        new { diagnostic.Code, diagnostic.Position.Line, diagnostic.Position.Column }
    };
    var tokens = editor.Tokens.Where(token => token.Kind != "end").Select(token =>
        new { token.Kind, token.Text, token.Value, token.Offset, token.EndOffset,
            token.Position.Line, token.Position.Column });
    var functions = MacroApi.Signatures.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.Select(signature => new {
            Result = signature.Result.ToString(),
            Parameters = signature.Parameters.Select(parameter => parameter.ToString()).ToArray()
        }).ToArray());
    return new { tokens, diagnostics, functions, members, symbols };
}

internal sealed record EditorRequest(string Path, string Source, bool ConstantsFile,
    Dictionary<string, string>? Constants, string? Operation, int? Offset);
