using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal sealed class MacroProgram : IDisposable
{
    private readonly ResourceQuota memory;
    private ResourceReservation? pendingMemory;
    private HashSet<string>? capturedStrings;
    private long snapshotBytes;

    private MacroProgram(string fullPath, string source, MacroBlock body,
        Dictionary<string, MacroFunction> functions, int nodes, ResourceQuota memory)
    {
        FullPath = fullPath;
        Source = source;
        Body = body;
        Functions = functions;
        NodeCount = nodes;
        this.memory = memory;
        snapshotBytes = source.Length * 4L + nodes * 64L;
    }

    public string FullPath { get; }
    internal string Source { get; }
    internal MacroBlock Body { get; }
    internal Dictionary<string, MacroFunction> Functions { get; }
    internal int NodeCount { get; }
    internal string? SourceDigest { get; private set; }
    internal IReadOnlyList<MacroConstantsBinding> Constants { get; private set; } = [];

    public static async Task<MacroProgram> LoadAsync(string path, CancellationToken token = default)
        => await LoadForLaunchAsync(path, true, _ => Task.FromResult(false), token).ConfigureAwait(false)
            ?? throw new MacroSourceException("ConstantsRead", new(1, 1), path);

    internal static async Task<MacroProgram?> LoadForLaunchAsync(string path, bool sourceReadAuthorized,
        Func<string, Task<bool>> confirmRead, CancellationToken token = default)
    {
        path = ResolveSourcePath(path);
        var quota = NewQuota();
        using var retained = quota.Reserve(0);
        try
        {
            var macro = await CaptureAsync(path, sourceReadAuthorized, confirmRead, MacroLimits.SourceBytes,
                "SourceLimit", quota, retained, token).ConfigureAwait(false);
            if (macro is null) return null;
            var program = ParseSource(path, macro.Text, quota, retained, out var declarations);
            program.SourceDigest = macro.Digest;

            int bytesRemaining = MacroLimits.SourceBytes;
            int membersRemaining = MacroConstantsFile.MaxMembers;
            string directory = Path.GetDirectoryName(path)!;
            var files = new Dictionary<string, MacroConstantsFile>(StringComparer.Ordinal);
            var bindings = new List<MacroConstantsBinding>(declarations.Count);
            foreach (var declaration in declarations)
            {
                token.ThrowIfCancellationRequested();
                string fullPath;
                try
                {
                    MacroLimits.Unicode(declaration.Path);
                    fullPath = OrdinaryWindowsPath.Resolve(declaration.Path, directory);
                }
                catch (Exception error) when (error is MacroError or InvalidDataException or ArgumentException or PathTooLongException)
                { throw new MacroSourceException("Path", declaration.Position, path); }

                if (!files.TryGetValue(fullPath, out var file))
                {
                    CapturedText? captured;
                    try
                    {
                        captured = await CaptureAsync(fullPath, false, confirmRead, bytesRemaining,
                            "ConstantsLimit", quota, retained, token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not ResourceLimitException &&
                        error is IOException or UnauthorizedAccessException or NotSupportedException)
                    { throw new MacroSourceException("ConstantsRead", new(1, 1), fullPath); }
                    if (captured is null) return null;
                    bytesRemaining -= captured.ByteCount;

                    // Reserve for temporary lexer tokens and string decoding. Retained
                    // string values and names fit in the original UTF-16 character count.
                    using var scratch = quota.Reserve(512 + captured.Text.Length * 6L);
                    retained.Add(128 + captured.Text.Length * 2L);
                    file = MacroConstantsFile.Parse(fullPath, captured.Digest, captured.Text,
                        membersRemaining, () => retained.Add(224));
                    files.Add(fullPath, file);
                    membersRemaining -= file.Values.Count;
                    foreach (var value in file.Values.Values)
                    {
                        if (value is string text)
                            (program.capturedStrings ??= new(ReferenceEqualityComparer.Instance)).Add(text);
                    }
                }
                retained.Add(128);
                bindings.Add(new(declaration.Alias, file));
            }
            program.Constants = bindings.AsReadOnly();
            new MacroChecker(program).Check();
            token.ThrowIfCancellationRequested();
            program.snapshotBytes = retained.Size;
            program.pendingMemory = retained.Separate(retained.Size);
            return program;
        }
        catch (ResourceLimitException) { throw new MacroSourceException("MemoryLimit", new(1, 1), path); }
        catch (MacroSourceException error) when (error.FilePath is null)
        { throw new MacroSourceException(error.Code, error.Position, path); }
    }

    public static MacroProgram Parse(string path, string source)
    {
        var program = ParseSource(Path.GetFullPath(path), source, NewQuota(), null, out var declarations);
        if (declarations.Count != 0)
            throw new MacroSourceException("ConstantsRead", declarations[0].Position, program.FullPath);
        new MacroChecker(program).Check();
        return program;
    }

    internal static List<MacroEditorSymbol> AnalyzeForEditor(string path, string source,
        IReadOnlyDictionary<string, string> constants, MacroEditorSymbols editor, int? offset)
    {
        using var program = ParseSource(Path.GetFullPath(path), source, NewQuota(), null, out var declarations, editor);
        var bindings = new List<MacroConstantsBinding>();
        int remaining = MacroConstantsFile.MaxMembers;
        foreach (var declaration in declarations)
        {
            if (!constants.TryGetValue(declaration.Alias, out var text))
            {
                editor.Recover(new MacroSourceException("ConstantsRead", declaration.Position, path));
                continue;
            }
            MacroConstantsFile file;
            try
            {
                file = MacroConstantsFile.Parse(declaration.Path, "", text, remaining, () => { });
            }
            catch (MacroSourceException)
            {
                editor.Recover(new MacroSourceException("ConstantsInvalid", declaration.Position, path));
                continue;
            }
            remaining -= file.Values.Count;
            bindings.Add(new(declaration.Alias, file));
        }
        program.Constants = bindings.AsReadOnly();
        try { new MacroChecker(program, editor).Check(); }
        catch (MacroSourceException error) when (editor.Recover(error)) { }
        return offset.HasValue ? editor.At(program, offset.Value) : [];
    }

    private static MacroProgram ParseSource(string path, string source, ResourceQuota quota,
        ResourceReservation? retained, out IReadOnlyList<MacroConstantsDeclaration> declarations,
        MacroEditorSymbols? editor = null)
    {
        if (source.Length > MacroLimits.StringUnits) throw new MacroSourceException("SourceLimit", new(1, 1), path);
        using var tokens = quota.Reserve(512 + source.Length * 4L);
        retained?.Add(source.Length * 2L);
        var budget = new MacroSyntaxBudget(tokens, retained);
        var parser = new MacroParser(source, new(1, 1), budget, editor);
        var (body, functions) = parser.ParseFile();
        declarations = parser.ConstantsDeclarations;
        return new(path, source, body, functions, budget.Nodes, quota);
    }

    internal static string ResolveSourcePath(string path)
    {
        try
        {
            MacroLimits.Unicode(path);
            return OrdinaryWindowsPath.Resolve(path);
        }
        catch (Exception error) when (error is MacroError or InvalidDataException or ArgumentException or PathTooLongException)
        { throw new MacroSourceException("Path", new(1, 1)); }
    }

    private sealed record CapturedText(string Text, string Digest, int ByteCount);

    private static async Task<CapturedText?> CaptureAsync(string path, bool alreadyAuthorized,
        Func<string, Task<bool>> confirmRead, int remaining, string limitCode, ResourceQuota quota,
        ResourceReservation retained, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SafeFileHandle? local = alreadyAuthorized ? null : LocalMacroFile.TryOpen(path);
        if (!alreadyAuthorized && local is null && !await confirmRead(path).ConfigureAwait(false)) return null;
        token.ThrowIfCancellationRequested();
        using SafeFileHandle? authorized = local is null
            ? File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous)
            : null;
        SafeFileHandle handle = local ?? authorized!;
        if (!LocalMacroFile.IsOrdinaryFile(handle)) throw new MacroSourceException("Path", new(1, 1), path);
        long length = RandomAccess.GetLength(handle);
        if (length > remaining) throw new MacroSourceException(limitCode, new(1, 1), path);
        using var buffer = quota.Reserve(256 + length);
        var bytes = new byte[(int)length];
        DateTime stamp = File.GetLastWriteTimeUtc(handle);
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = await RandomAccess.ReadAsync(handle, bytes.AsMemory(offset), offset, token).ConfigureAwait(false);
            if (count == 0) throw new MacroSourceException("SourceChanged", new(1, 1), path);
            offset += count;
        }
        if (RandomAccess.GetLength(handle) != length || File.GetLastWriteTimeUtc(handle) != stamp)
            throw new MacroSourceException("SourceChanged", new(1, 1), path);
        var (encoding, skip) = DetectEncoding(bytes);
        try
        {
            int chars = encoding.GetCharCount(bytes, skip, bytes.Length - skip);
            retained.Add(256 + path.Length * 2L + chars * 2L);
            return new(encoding.GetString(bytes, skip, bytes.Length - skip),
                Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        }
        catch (DecoderFallbackException) { throw new MacroSourceException("SourceEncoding", new(1, 1), path); }
    }

    internal static (Encoding Encoding, int Skip) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (new UTF32Encoding(false, true, true), 4);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (new UTF32Encoding(true, true, true), 4);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(false, true), 3);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, true, true), 2);
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, true, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    private static ResourceQuota NewQuota() => new(MacroLimits.RunBytes, MacroLimits.ApplicationMemory);

    internal bool OwnsConstantString(string text) => capturedStrings?.Contains(text) == true;

    internal IDisposable ReserveReview(long bytes)
    {
        try { return memory.Reserve(bytes); }
        catch (ResourceLimitException) { throw new MacroSourceException("MemoryLimit", new(1, 1), FullPath); }
    }

    internal ResourceReservation AdmitRun(out ResourceQuota runMemory)
    {
        lock (this)
        {
            // The run takes the existing reservation so admission cannot double-charge
            // its source, constants and AST at the transition out of review.
            if (pendingMemory is { } captured)
            {
                captured.Add(MacroLimits.ReserveBytes);
                pendingMemory = null;
                runMemory = memory;
                return captured;
            }
            runMemory = NewQuota();
            return runMemory.Reserve(MacroLimits.ReserveBytes + snapshotBytes);
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            pendingMemory?.Dispose();
            pendingMemory = null;
        }
    }
}
