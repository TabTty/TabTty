using System.Text;
using TabTty.Core;

internal static partial class MacroContracts
{
    private static async Task SharedConstantsLanguageAsync()
    {
        using var directory = new TemporaryDirectory();
        string constants = Path.Combine(directory.Path, "common.tttyc");
        string source = Path.Combine(directory.Path, "inspect.tttym");
        string other = Path.Combine(directory.Path, "startup.tttym");
        const string definitions = """
            // Shared values
            string Prompt = "日本語😀";
            var Ok = true;
            long Min = -9_223_372_036_854_775_808L;
            long Max = +9223372036854775807;
            var Exponent = 1e0;
            double Promoted = 1L;
            string Path = @"profiles\startup.ttty";
            /* an unused definition */
            var unused = 1;
            """;
        const string header = "#tttym 1\r\n#constants Common \"common.tttyc\"\r\n";
        await File.WriteAllTextAsync(source, header + """
            string Describe() {
                long minimum = Common.Min;
                long maximum = Common.Max;
                return $"{Common.Prompt}|{Common.Ok}|{minimum}|{maximum}|{Common.Exponent}";
            }
            var constants = Describe();
            Dialog("", constants, DialogButtons.OK, false, "");
            """);
        await File.WriteAllTextAsync(other, "#tttym 1\n#constants Site\t@\"common.tttyc\" // shared\n" +
            "Dialog(\"\", Site.Prompt, DialogButtons.OK, false, \"\");");

        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            await File.WriteAllBytesAsync(constants, [.. encoding.Preamble, .. encoding.GetBytes(definitions)]);
            using var first = await LoadFixture(source);
            using var second = await LoadFixture(other);
            var values = first.Constants.Single().File.Values;
            Check(values["Promoted"] is double and 1.0 && (string)values["Path"] == @"profiles\startup.ttty",
                "Explicit widening or verbatim path syntax differs from macro declarations.");
            File.Delete(constants);
            Check(await RunDialog(first) == "日本語😀|true|-9223372036854775808|9223372036854775807|1",
                "The shared constants changed after capture or lost scalar precision.");
            Check(await RunDialog(second) == "日本語😀", "The second macro could not read the same constants file.");
        }

        await File.WriteAllTextAsync(constants, """var Prompt = "\uD83D\uDE00"; bool prompt = false;""");
        using (var escaped = await LoadFixture(other))
            Check(await RunDialog(escaped) == "😀", "Escaped strings or case-sensitive names failed to bind.");
        await File.WriteAllTextAsync(source, header + "var constants = 12;");
        await File.WriteAllTextAsync(constants, "/* Empty file */");
        using (var empty = await LoadFixture(source))
            Check(empty.Constants.Single().File.Values.Count == 0, "A file with no declarations is valid.");
        await File.WriteAllTextAsync(constants, definitions);
        foreach (string invalid in new[]
        {
            header + "Common.Min = 1;", header + "Common.Min++;", header + "var rejected = Common;",
            header + "void Unused() { long wrong = Common.Exponent; }",
            header + "void Unused() { string missing = Common.prompt; }",
            header + "var Common = 1;", header + "void Common() {}", header + "void F(string Common) {}",
            header + "#constants Common \"common.tttyc\"",
            "#tttym 1\n#constants Macro \"common.tttyc\"", "#tttym 1\n#constants Site $\"common.tttyc\"",
            "#tttym 1\n#constants Site \"common.tttyc\";", "#tttym 1\n#constants /*gap*/ Site \"common.tttyc\"",
            "#tttym 1\n#constants Site @\"common\n.tttyc\"", "#tttym 1\nvar n = 1;\n#constants Site \"common.tttyc\"",
        })
        {
            await File.WriteAllTextAsync(source, invalid);
            await Reject(source);
        }

        await File.WriteAllTextAsync(source, header + "string prompt = Common.Prompt;");
        foreach (string invalid in new[]
        {
            """{"Prompt":"old JSON is rejected"}""", """string Prompt = "ok"; var Unused = [];""",
            """string Prompt = "ok"; var Unused = {};""", """string Prompt = "ok"; var Unused = null;""",
            """string Prompt = "ok"; var Unused = 9223372036854775808;""",
            """string Prompt = "ok"; var Unused = +9223372036854775808;""",
            """string Prompt = "ok"; var Unused = 1e999;""", """string Prompt = "ok"; var Macro = 42;""",
            """string Prompt = "ok"; var a-b = 1;""", """string Prompt = "ok" """,
            """string Prompt = "ok"; Prompt = "new";""", """string Prompt = "ok"; List<long> Unused = [];""",
            """string Prompt = "ok"; long Unused = 1.0;""", """string Prompt = "ok"; bool Unused = 1;""",
            """string Prompt = "ok"; string Unused = true;""", """string Prompt = "ok"; var Unused = 5 * 1000;""",
            """string Prompt = "ok"; var Unused = Prompt;""", """string Prompt = "ok"; var Unused = Format(1);""",
            """string Prompt = "ok"; var Unused = $"value";""", """string Prompt = "ok"; long Unused;""",
            """string Prompt = "ok"; Sleep(5);""", "#tttym 1\nstring Prompt = \"header\";",
        })
        {
            await File.WriteAllTextAsync(constants, invalid);
            var error = await Reject(source);
            Check(error.FilePath == constants && error.Position.Line > 0 && error.Position.Column > 0,
                "The diagnostic did not identify the invalid constants file and position.");
        }
        await File.WriteAllTextAsync(constants, "// Duplicate\rstring Prompt = \"😀\"; bool Prompt = false;");
        var duplicate = await Reject(source);
        Check(duplicate.Code == "ConstantsName" && duplicate.Position == new MacroPosition(2, 28),
            $"Duplicate name diagnostic used the wrong UTF-16 location: {duplicate.Position}.");
        foreach (byte[] malformed in new byte[][] { [0xff, 0xfe, 0x00], Encoding.Unicode.GetBytes(definitions), [0xc0, 0xff] })
        {
            await File.WriteAllBytesAsync(constants, malformed);
            var error = await Reject(source);
            Check(error.FilePath == constants, "A decoding error did not identify the dependency.");
        }

        static async Task<string> RunDialog(MacroProgram program)
        {
            var host = new Host();
            using var run = new MacroRun(program, [], host);
            run.Start();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(run.Status.State == MacroRunState.Completed, "Shared constant run failed: " + run.Status);
            return host.Dialogs.Single().Message;
        }

        async Task<MacroProgram> LoadFixture(string path) =>
            await MacroProgram.LoadForLaunchAsync(path, true, candidate => Task.FromResult(candidate == constants))
            ?? throw new InvalidOperationException("The macro requested an unexpected dependency.");

        async Task<MacroSourceException> Reject(string path)
        {
            try { using var unexpected = await LoadFixture(path); }
            catch (MacroSourceException error) { return error; }
            throw new InvalidOperationException("Invalid constants declaration or definition was accepted.");
        }
    }
}
