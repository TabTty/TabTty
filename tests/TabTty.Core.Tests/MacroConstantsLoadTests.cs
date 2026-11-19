using TabTty.Core;

internal static class MacroConstantsLoadTests
{
    internal static async Task RunAsync()
    {
        using var directory = new TemporaryDirectory();
        string macro = Path.Combine(directory.Path, "bounded.tttym");
        string first = Path.Combine(directory.Path, "first.json");
        string second = Path.Combine(directory.Path, "second.json");
        const int maxBytes = 1024 * 1024;
        const string header = "#tttym 1\n";
        const string both = "#constants First \"first.json\"\n#constants Second \"second.json\"\n";

        await File.WriteAllTextAsync(first, "{}".PadRight(maxBytes));
        string aliases = string.Concat(Enumerable.Range(0, 16).Select(i =>
            $"#constants Alias{i} \"{(i % 2 == 0 ? "first.json" : "./first.json")}\"\n"));
        await File.WriteAllTextAsync(macro, header + aliases);
        using (var program = await MacroProgram.LoadAsync(macro))
        {
            if (program.Constants.Count != 16 ||
                program.Constants.Any(binding => !ReferenceEquals(binding.File, program.Constants[0].File)))
                throw new Exception("A repeated normalized path consumed extra snapshots or lost an alias.");
        }
        await File.AppendAllTextAsync(macro, "#constants Extra \"first.json\"\n");
        await RejectAsync(macro, "ConstantsLimit");
        await File.WriteAllTextAsync(macro, header + "#constants First \"first.json\"\n");
        await File.AppendAllTextAsync(first, " ");
        await RejectAsync(macro, "ConstantsLimit", first);

        await File.WriteAllTextAsync(first, "{}".PadRight(maxBytes / 2));
        await File.WriteAllTextAsync(second, "{}".PadRight(maxBytes / 2));
        await File.WriteAllTextAsync(macro, header + both);
        using (var exact = await MacroProgram.LoadAsync(macro))
        {
            if (exact.Constants.Count != 2) throw new Exception("Aggregate byte limit rejected an exact fit.");
        }
        await File.AppendAllTextAsync(second, " ");
        await RejectAsync(macro, "ConstantsLimit", second);

        string members = string.Join(',', Enumerable.Range(0, 4095).Select(i => $"\"Key{i}\":{i}"));
        await File.WriteAllTextAsync(first, "{" + members + "}");
        await File.WriteAllTextAsync(second, "{\"Tail\":true}");
        using (var exact = await MacroProgram.LoadAsync(macro))
        {
            if (exact.Constants.Sum(binding => binding.File.Values.Count) != 4096)
                throw new Exception("Aggregate member limit rejected an exact fit.");
        }
        await File.WriteAllTextAsync(second, "{\"Tail\":true,\"Extra\":false}");
        await RejectAsync(macro, "ConstantsLimit", second);
        File.Delete(second);
        await RejectAsync(macro, "ConstantsRead", second);

        // A copied macro can use local relative definitions regardless of the process directory.
        string moved = Directory.CreateDirectory(Path.Combine(directory.Path, "moved")).FullName;
        await File.WriteAllTextAsync(Path.Combine(moved, "first.json"), "{\"Value\":false}");
        await File.WriteAllTextAsync(first, "{\"Value\":true}");
        string movedMacro = Path.Combine(moved, "bounded.tttym");
        await File.WriteAllTextAsync(movedMacro, header + "#constants Relative \"first.json\"\n" +
            $"#constants Absolute @\"{first}\"\nSetBaseDirectory(\"..\");");
        using (var movedProgram = await MacroProgram.LoadAsync(movedMacro))
        {
            if ((bool)movedProgram.Constants[0].File.Values["Value"] || !(bool)movedProgram.Constants[1].File.Values["Value"])
                throw new Exception("The launch bindings did not respect relative and absolute paths.");
        }

        const string remote = @"\\localhost\TabTty-Macro-Constants-Test\missing.json";
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

        await File.WriteAllTextAsync(macro, header + "#constants First \"first.json\"\n");
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
