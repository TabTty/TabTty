using System.Text;
using TabTty.Core;

internal static partial class MacroContracts
{
    private static async Task SharedConstantsLanguageAsync()
    {
        using var directory = new TemporaryDirectory();
        string constants = Path.Combine(directory.Path, "common.json");
        string source = Path.Combine(directory.Path, "inspect.tttym");
        string other = Path.Combine(directory.Path, "startup.tttym");
        const string header = "#tttym 1\r\n#constants Common \"common.json\"\r\n";
        const string json = """
            {"Prompt":"日本語😀","Ok":true,"Min":-9223372036854775808,"Max":9223372036854775807,"Exponent":1e0,"unused":1}
            """;
        await File.WriteAllTextAsync(source, header + """
            string Describe() {
                long minimum = Common.Min;
                long maximum = Common.Max;
                return $"{Common.Prompt}|{Common.Ok}|{minimum}|{maximum}|{Common.Exponent}";
            }
            var constants = Describe();
            Dialog("", constants, DialogButtons.OK, false, "");
            """);
        await File.WriteAllTextAsync(other, "#tttym 1\n#constants Site\t@\"common.json\" // shared\n" +
            "Dialog(\"\", Site.Prompt, DialogButtons.OK, false, \"\");");

        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            await File.WriteAllBytesAsync(constants, [.. encoding.Preamble, .. encoding.GetBytes(json)]);
            using var first = await LoadFixture(source);
            using var second = await LoadFixture(other);
            File.Delete(constants);
            Check(await RunDialog(first) == "日本語😀|true|-9223372036854775808|9223372036854775807|1",
                "The shared constants changed after capture or lost scalar precision.");
            Check(await RunDialog(second) == "日本語😀", "The second macro could not read the same constants file.");
        }

        await File.WriteAllTextAsync(constants, """{"\u0050rompt":"\uD83D\uDE00","prompt":false}""");
        using (var escaped = await LoadFixture(other))
            Check(await RunDialog(escaped) == "😀", "Escaped keys or strings failed to bind.");
        await File.WriteAllTextAsync(source, header + "var constants = 12;");
        await File.WriteAllTextAsync(constants, "{}");
        using (var empty = await LoadFixture(source))
            Check(empty.Constants.Single().File.Values.Count == 0, "An empty object is a valid constants file.");
        await File.WriteAllTextAsync(constants, json);
        foreach (string invalid in new[]
        {
            header + "Common.Min = 1;", header + "Common.Min++;", header + "var rejected = Common;",
            header + "void Unused() { long wrong = Common.Exponent; }",
            header + "void Unused() { string missing = Common.prompt; }",
            header + "var Common = 1;", header + "void Common() {}", header + "void F(string Common) {}",
            header + "#constants Common \"common.json\"",
            "#tttym 1\n#constants Macro \"common.json\"", "#tttym 1\n#constants Site $\"common.json\"",
            "#tttym 1\n#constants Site \"common.json\";", "#tttym 1\n#constants /*gap*/ Site \"common.json\"",
            "#tttym 1\n#constants Site @\"common\n.json\"", "#tttym 1\nvar n = 1;\n#constants Site \"common.json\"",
        })
        {
            await File.WriteAllTextAsync(source, invalid);
            await Reject(source);
        }

        await File.WriteAllTextAsync(source, header + "string prompt = Common.Prompt;");
        foreach (string invalid in new[]
        {
            """{"Prompt":"ok","\u0050rompt":"duplicate"}""", """{"Prompt":"ok","Unused":[]}""",
            """{"Prompt":"ok","Unused":{}}""", """{"Prompt":"ok","Unused":null}""",
            """{"Prompt":"ok","Unused":9223372036854775808}""", """{"Prompt":"ok","Unused":1e999}""",
            """{"Prompt":"ok","Unused":"\uD800"}""", """{"Prompt":"ok","Macro":42}""",
            """{"Prompt":"ok","a-b":1}""", """{"Prompt":"ok",}""", """{"Prompt":"ok"} {}""",
            """{"Prompt":"ok"/*tail*/}""", """["Prompt"]""",
        })
        {
            await File.WriteAllTextAsync(constants, invalid);
            var error = await Reject(source);
            Check(error.FilePath == constants && error.Position.Line > 0 && error.Position.Column > 0,
                "The diagnostic did not identify the invalid constants file and position.");
        }
        await File.WriteAllTextAsync(constants, "{\r\"Prompt\":\"😀\", \"Prompt\": false}");
        var duplicate = await Reject(source);
        Check(duplicate.Code == "ConstantsName" && duplicate.Position == new MacroPosition(2, 16),
            $"Duplicate JSON key diagnostic used the wrong UTF-16 location: {duplicate.Position}.");
        foreach (byte[] malformed in new byte[][] { [0xff, 0xfe, 0x00], Encoding.Unicode.GetBytes(json), [0xc0, 0xff] })
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
