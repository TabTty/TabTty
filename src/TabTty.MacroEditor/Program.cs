using System.Text.Json;
using TabTty.Core;

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
while (Console.ReadLine() is { } line)
{
    try
    {
        var request = JsonSerializer.Deserialize<EditorRequest>(line, jsonOptions)
            ?? throw new InvalidDataException("Missing request");
        object result = request.Operation switch
        {
            "readLocal" => ReadLocal(request.Path),
            "matchOpenLocal" => MatchOpenLocal(request),
            _ => Analyze(request)
        };
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

static object MatchOpenLocal(EditorRequest request)
{
    string? stored = StoredPath(request.Path);
    string? match = stored is null ? null : request.Candidates?.FirstOrDefault(candidate =>
        candidate.Equals(request.Path, StringComparison.OrdinalIgnoreCase) && StoredPath(candidate) == stored);
    return new { match };

    static string? StoredPath(string path)
    {
        try { return LocalMacroFile.StoredLocalPath(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}

static object Analyze(EditorRequest request)
{
    var editor = new MacroEditorSymbols(request.Offset);
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
                    editor.ConstantDeclaration(name, type);
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
    catch (ResourceLimitException) { diagnostic = new("MemoryLimit", new(1, 1)); }
    var errors = editor.DependencyErrors;
    if (diagnostic is not null && !errors.Contains(diagnostic)) errors.Add(diagnostic);
    var diagnostics = errors.Select(error => new { error.Code, error.Position.Line, error.Position.Column,
        error.Length, error.WaitPatternFix });
    var functions = MacroApi.Signatures.ToDictionary(
        pair => pair.Key,
        pair => {
            var namedParameters = MacroApi.NamedSignature(pair.Key)?.NamedParameters
                ?.Select(p => new { p.Name, Type = p.Type.ToString(), p.Index }).ToArray();
            return pair.Value.Select(signature => new {
                Result = signature.Result.ToString(),
                NamedParameters = namedParameters,
                Parameters = signature.Parameters.Select(parameter => parameter.ToString()).ToArray()
            }).ToArray();
        });
    var activeCall = editor.ActiveCall is { } call ? new {
        name = call.Name, parameter = call.Parameter, usedSlots = call.UsedSlots, namedSlot = call.NamedSlot,
        completeOption = call.CompleteOption, hasOptionColon = call.HasOptionColon
    } : null;
    return new { unsupportedVersion = editor.UnsupportedVersion, diagnostics, functions, members, symbols, activeCall,
        versionInsertion = editor.VersionInsertion, dependencies = editor.Dependencies,
        constantCompletions = editor.ConstantCompletions, reference = editor.Reference };
}

internal sealed record EditorRequest(string Path, string Source, bool ConstantsFile,
    Dictionary<string, string>? Constants, string? Operation, int? Offset, string[]? Candidates);
