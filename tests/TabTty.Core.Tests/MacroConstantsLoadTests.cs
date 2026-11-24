using TabTty.Core;

internal static class MacroConstantsLoadTests
{
    internal static async Task RunAsync()
    {
        using var directory = new TemporaryDirectory();
        string macro = Path.Combine(directory.Path, "bounded.tttym");
        string first = Path.Combine(directory.Path, "first.tttyc");
        string second = Path.Combine(directory.Path, "second.tttyc");
        const int maxBytes = 1024 * 1024;
        const string header = "#tttym 1\n";
        const string both = "#constants First \"first.tttyc\"\n#constants Second \"second.tttyc\"\n";

        await File.WriteAllTextAsync(first, new string(' ', maxBytes));
        string aliases = string.Concat(Enumerable.Range(0, 16).Select(i =>
            $"#constants Alias{i} \"{(i % 2 == 0 ? "first.tttyc" : "./first.tttyc")}\"\n"));
        await File.WriteAllTextAsync(macro, header + aliases);
        using (var program = await MacroProgram.LoadAsync(macro))
        {
            if (program.Constants.Count != 16 ||
                program.Constants.Any(binding => !ReferenceEquals(binding.File, program.Constants[0].File)))
                throw new Exception("A repeated normalized path consumed extra snapshots or lost an alias.");
        }
        await File.AppendAllTextAsync(macro, "#constants Extra \"first.tttyc\"\n");
        await RejectAsync(macro, "ConstantsLimit");
        await File.WriteAllTextAsync(macro, header + "#constants First \"first.tttyc\"\n");
        await File.AppendAllTextAsync(first, " ");
        await RejectAsync(macro, "ConstantsLimit", first);

        await File.WriteAllTextAsync(first, "bool First = true;".PadRight(maxBytes / 2));
        await File.WriteAllTextAsync(second, "bool Second = false;".PadRight(maxBytes / 2));
        await File.WriteAllTextAsync(macro, header + both);
        using (var exact = await MacroProgram.LoadAsync(macro))
        {
            if (exact.Constants.Count != 2) throw new Exception("Aggregate byte limit rejected an exact fit.");
        }
        var buffers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [first] = "bool First = true;",
            [second] = "bool Second = false;",
        };
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [first] = maxBytes / 2,
            [second] = maxBytes / 2,
        };
        const string alias = "#constants Alias \"./first.tttyc\"\n";
        string source = header + alias + both + "#constants Unsaved \"unsaved.tttyc\"\n";
        buffers[Path.Combine(directory.Path, "unsaved.tttyc")] = "bool Extra = true;";
        var successfulEditor = new MacroEditorSymbols(null);
        MacroProgram.AnalyzeForEditor(macro, source, buffers, successfulEditor, null, sizes);
        if (successfulEditor.DependencyErrors.Count != 0) throw new Exception("Editor rejected an exact byte fit with an unknown size.");
        sizes[second]++;
        var overBudgetEditor = new MacroEditorSymbols(null);
        MacroProgram.AnalyzeForEditor(macro, source, buffers, overBudgetEditor, null, sizes);
        if (overBudgetEditor.DependencyErrors.Single() is not { Code: "ConstantsLimit", Position.Line: 4 })
            throw new Exception("The editor did not report the first directive that exceeded the known byte budget.");
        await File.AppendAllTextAsync(second, " ");
        await RejectAsync(macro, "ConstantsLimit", second);

        string members = string.Join('\n', Enumerable.Range(0, 4095).Select(i => $"long Key{i} = {i};"));
        await File.WriteAllTextAsync(first, members);
        await File.WriteAllTextAsync(second, "bool Tail = true;");
        using (var exact = await MacroProgram.LoadAsync(macro))
        {
            if (exact.Constants.Sum(binding => binding.File.Values.Count) != 4096)
                throw new Exception("Aggregate member limit rejected an exact fit.");
        }
        await File.WriteAllTextAsync(second, "bool Tail = true; bool Extra = false;");
        await RejectAsync(macro, "ConstantsLimit", second);
        File.Delete(second);
        await RejectAsync(macro, "ConstantsRead", second);

        // A copied macro can use local relative definitions regardless of the process directory.
        string moved = Directory.CreateDirectory(Path.Combine(directory.Path, "moved")).FullName;
        await File.WriteAllTextAsync(Path.Combine(moved, "first.tttyc"), "bool Value = false;");
        await File.WriteAllTextAsync(first, "bool Value = true;");
        string movedMacro = Path.Combine(moved, "bounded.tttym");
        await File.WriteAllTextAsync(movedMacro, header + "#constants Relative \"first.tttyc\"\n" +
            $"#constants Absolute @\"{first}\"\nSetBaseDirectory(\"..\");");
        using (var movedProgram = await MacroProgram.LoadAsync(movedMacro))
        {
            if ((bool)movedProgram.Constants[0].File.Values["Value"] || !(bool)movedProgram.Constants[1].File.Values["Value"])
                throw new Exception("The launch bindings did not respect relative and absolute paths.");
        }

        const string remote = @"\\localhost\TabTty-Macro-Constants-Test\missing.tttyc";
        await File.WriteAllTextAsync(macro, header + $"#constants Remote @\"{remote}\"\n");
        long before = MacroLimits.ApplicationMemory.Used;
        int requests = 0;
        using var cancelled = await MacroProgram.LoadForLaunchAsync(macro, true, path =>
        {
            if (path != remote) throw new Exception("Unexpected read consent path.");
            requests++;
            return Task.FromResult(false);
        });
        if (cancelled is not null || requests != 1 || MacroLimits.ApplicationMemory.Used != before)
            throw new Exception("Cancelling dependency read did not release the pending launch.");

        await File.WriteAllTextAsync(macro, header + "#constants First \"first.tttyc\"\n");
        using (var firstReview = await MacroProgram.LoadAsync(macro))
        using (var secondReview = await MacroProgram.LoadAsync(macro))
        {
            if (MacroLimits.ApplicationMemory.Used <= before)
                throw new Exception("Pending reviews did not account for captured definitions.");
            try
            {
                using var excess = secondReview.ReserveReview(MacroLimits.RunBytes);
                throw new Exception("Review exceeded its private budget.");
            }
            catch (MacroSourceException error) when (error.Code == "MemoryLimit") { }
            using var firstCopy = firstReview.ReserveReview(1024 * 1024);
            using var pressure = MacroLimits.ApplicationMemory.Reserve(
                256L * 1024 * 1024 - MacroLimits.ApplicationMemory.Used - 512);
            await RejectAsync(macro, "MemoryLimit");
            firstCopy.Dispose();
            firstReview.Dispose();
            using var retry = await MacroProgram.LoadAsync(macro);
        }
        if (MacroLimits.ApplicationMemory.Used != before)
            throw new Exception("Memory remained charged after all macro reviews ended.");
    }

    private static async Task RejectAsync(string path, string code, string? diagnosticPath = null)
    {
        try { using var unexpected = await MacroProgram.LoadAsync(path); }
        catch (MacroSourceException error) when (error.Code == code &&
            (diagnosticPath is null || error.FilePath == diagnosticPath)) { return; }
        throw new Exception("Expected macro load error " + code);
    }
}
