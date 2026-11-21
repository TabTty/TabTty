using System.Diagnostics;
using System.IO;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task SharedConstantsCloudAsync(App app, string directory)
    {
        string root = Path.Combine(directory, "cloud");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--macro-cloud-provider");
        start.ArgumentList.Add(root);
        using var provider = Process.Start(start) ?? throw new IOException("Could not start cloud test provider.");
        var window = app.CreateWindow(restorePlacement: false);
        try
        {
            CheckMacroLaunch(await ReadReply() == "ready", "Cloud Files registration or placeholder creation failed.");
            window.Show();
            string source = Path.Combine(directory, "cloud.tttym");
            var association = InstanceLaunch.Parse([source]).Single();
            var cli = InstanceLaunch.Parse(["--macro", source, "--", "cli"]).Single();
            var forwarded = InstanceActivation.DecodeRequests(InstanceActivation.EncodeRequests([cli]))!.Single();
            Func<Task>[] routes = [() => window.RunMacroAsync(source, null),
                () => window.OpenExternalMacroAsync(association), () => window.OpenExternalMacroAsync(cli),
                () => window.OpenExternalMacroAsync(forwarded)];
            for (int i = 0; i < routes.Length; i++)
            {
                string dependency = Path.Combine(root, $"online-{i}.tttyc");
                File.WriteAllText(source, $$"""
                    #tttym 1
                    #constants Shared @"{{dependency}}"
                    if (Shared.Marker != "hydrated") { var fail = 1 / 0; }
                    """);
                int before = await FetchCount();
                var priorTabs = window.TerminalTabs.ToHashSet();
                Task launch = routes[i]();
                await DecideMacroRequest(app, window, dependency, null);
                await launch.WaitAsync(TimeSpan.FromSeconds(10));
                int afterCancellation = await FetchCount();
                CheckMacroLaunch(afterCancellation == before && priorTabs.SetEquals(window.TerminalTabs),
                    $"Cloud route {i}: cancel changed fetch count {before} to {afterCancellation} or opened a tab.");

                launch = routes[i]();
                await DecideMacroRequest(app, window, dependency, null, "MacroReadSource");
                if (i == 0) await AcceptFixtureMacroArguments(app, window);
                await DecideMacroRequest(app, window, source, i < 2 ? App.Text("MacroNoArguments") : "[0] \"cli\"",
                    "MacroRunOnce");
                await launch.WaitAsync(TimeSpan.FromSeconds(10));
                var run = window.TerminalTabs.Single(tab => !priorTabs.Contains(tab)).Macro!;
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                CheckMacroLaunch(await FetchCount() > before && run.Status.State == MacroRunState.Completed,
                    "Read consent did not hydrate and execute the reviewed constants.");
            }
            Console.WriteLine("PASS CloudConstantsReadConsentThroughFourLaunchRoutes");
        }
        finally
        {
            window.Close();
            provider.StandardInput.Close();
            await provider.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            CheckMacroLaunch(provider.ExitCode == 0, "Cloud provider failed to clean up its disposable sync root.");
        }

        async Task<string?> ReadReply() => await provider.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        async Task<int> FetchCount()
        {
            await provider.StandardInput.WriteLineAsync("count");
            return int.Parse(await ReadReply() ?? throw new EndOfStreamException());
        }
    }
}
