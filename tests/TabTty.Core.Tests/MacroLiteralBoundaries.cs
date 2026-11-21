using System.Text;
using TabTty.Core;

internal static partial class MacroContracts
{
    private static async Task LiteralBoundariesAsync()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "literals.tttym");
        string constantsPath = Path.Combine(directory.Path, "shared.tttyc");
        await File.WriteAllTextAsync(constantsPath, """
            long Min = - /* sign */
                09_223_372_036_854_775_808L;
            string Esc = "\x1bD";
            """);
        await File.WriteAllTextAsync(path, """
            #tttym 1
            #constants Shared "shared\x2Etttyc"
            var exact = - /* sign */ 0009223372036854775808l == Shared.Min && Shared.Min == -9223372036854775808;
            try { var overflow = -Shared.Min; }
            catch (MacroError e) { Dialog("", $"{exact} {e.Code}", DialogButtons.OK, false, ""); }
            Connect("profile.ttty");
            Write("\x1BD");
            Write(Shared.Esc);
            Write($"\x01BD{Length("\u01BD")}");
            Write(@"\x1BD");
            Write($"{Length("")}{Length("x")}");
            """);
        var host = new Host { TransmitEncoding = "utf-8" };
        using var program = await MacroProgram.LoadForLaunchAsync(path, true, file => Task.FromResult(file == constantsPath))
            ?? throw new InvalidOperationException("The decoded directive selected the wrong path.");
        using var run = new MacroRun(program, [], host);
        try
        {
            run.Start();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(run.Status.State == MacroRunState.Completed && host.Dialogs.Single().Message == "true NumericOverflow",
                "The directly negated integer lost precision or its later negation did not overflow.");
            Check(host.Wire.Bytes.SequenceEqual(Encoding.UTF8.GetBytes("\u001bD\u001bD\u0001BD1" + @"\x1BD01")),
                "The session received the wrong bytes for a hexadecimal escape or adjacent interpolation expressions.");
        }
        finally { await host.CloseAsync(); }

        string verbatimDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "shared")).FullName;
        string verbatimTarget = Path.Combine(verbatimDirectory, "x2Etttyc");
        await File.WriteAllTextAsync(verbatimTarget, "long Marker = 2;");
        await File.WriteAllTextAsync(path, """
            #tttym 1
            #constants Shared @"shared\x2Etttyc"
            Sleep(Shared.Marker);
            """);
        using var verbatim = await MacroProgram.LoadForLaunchAsync(path, true, file => Task.FromResult(file == verbatimTarget));
        Check(verbatim?.Constants.Single().File.FullPath == verbatimTarget, "The verbatim directive interpreted its backslash.");

        foreach (var escape in new[] { @"\x", @"\x1", @"\xG1", @"\x1G", @"\x１1" })
        {
            string line = "#constants Shared \"😀" + escape + "\"";
            await File.WriteAllTextAsync(path, "#tttym 1\n" + line);
            try
            {
                using var unexpected = await MacroProgram.LoadForLaunchAsync(path, true,
                    _ => throw new InvalidOperationException("Invalid escape reached file access."));
                throw new InvalidOperationException("Invalid directive escape was accepted.");
            }
            catch (MacroSourceException error)
            {
                Check(error.Code == "StringEscape" && error.Position == new MacroPosition(2, line.IndexOf('\\') + 1),
                    "Launch did not locate the invalid escape at its backslash.");
            }
        }
        foreach (string integer in new[] { "+09223372036854775808", "09223372036854775808",
            "-(09223372036854775808)", "-09223372036854775809", "-09__223372036854775808L" })
        {
            try { using var unexpected = Parse("long invalid = " + integer + ";"); }
            catch (MacroSourceException error) when (error.Code == "NumericLiteral") { continue; }
            throw new InvalidOperationException("Invalid boundary literal was accepted: " + integer);
        }

        // At the 100,000-node limit each hole contributes its expression and
        // one empty text part. The trailing text, interpolation, declaration
        // and file block account for the remaining four nodes.
        string holes = string.Concat(Enumerable.Repeat("{true}", 49_998));
        using var atLimit = Parse("var text = $\"" + holes + "\";");
        try { using var unexpected = Parse("var text = $\"" + holes + "\"; return;"); }
        catch (MacroSourceException error) when (error.Code == "SyntaxLimit") { return; }
        throw new InvalidOperationException("Adjacent interpolation expressions lost their empty text node accounting.");
    }
}
