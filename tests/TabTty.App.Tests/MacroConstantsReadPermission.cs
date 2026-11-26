using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task SharedConstantsNetworkAsync(App app, string directory)
    {
        var window = app.CreateWindow(restorePlacement: false);
        window.Show();
        try
        {
            await SharedConstantsReadPermission(app, window, directory, liveSmb: true);
            string? mappedDrive = Environment.GetEnvironmentVariable("TABTTY_MACRO_TEST_MAPPED_DRIVE");
            if (mappedDrive is not null)
            {
                string fixture = Path.Combine(directory, "remote-constants.tttyc");
                await SharedConstantsReadPermission(app, window, directory, liveSmb: true, mappedDrive + fixture[2..]);
                Console.WriteLine("PASS SharedConstantsMappedDriveReadPermission");
            }
            string? redirect = Environment.GetEnvironmentVariable("TABTTY_MACRO_TEST_REDIRECT");
            if (redirect is not null)
            {
                await SharedConstantsReadPermission(app, window, directory, liveSmb: true, redirect);
                Console.WriteLine("PASS SharedConstantsRedirectReadPermission");
            }
        }
        finally { window.Close(); }
    }

    private static async Task SharedConstantsReadPermission(App app, MainWindow window, string directory, bool liveSmb,
        string? protectedPath = null)
    {
        string source = Path.Combine(directory, "remote-constants.tttym");
        string dependency = Path.Combine(directory, "remote-constants.tttyc");
        string remote = protectedPath ?? @"\\localhost\" + dependency[0] + "$" + dependency[2..];
        if (liveSmb)
        {
            File.WriteAllText(dependency, """long TimeoutMs = 0; string Marker = "fixture";""");
            CheckMacroLaunch(File.ReadAllText(remote) == File.ReadAllText(dependency),
                "The protected file fixture is unavailable.");
        }
        File.WriteAllText(source, $$"""
            #tttym 1
            #constants Common @"{{remote}}"
            if (Common.Marker != "fixture") { var fail = 1 / 0; }
            Sleep(Common.TimeoutMs);
            """);
        var association = InstanceLaunch.Parse([source]).Single();
        var commandLine = InstanceLaunch.Parse(["--macro", source, "--", "cli"]).Single();
        var forwarded = InstanceActivation.DecodeRequests(InstanceActivation.EncodeRequests([commandLine]))!.Single();

        int tabs = window.TerminalTabs.Count();
        await CheckRoute(() => window.RunMacroAsync(source, null), picked: true, App.Text("MacroNoArguments"));
        await CheckRoute(() => window.OpenExternalMacroAsync(association), picked: false, App.Text("MacroNoArguments"));
        await CheckRoute(() => window.OpenExternalMacroAsync(commandLine), picked: false, "[0] \"cli\"");
        await CheckRoute(() => window.OpenExternalMacroAsync(forwarded), picked: false, "[0] \"cli\"");
        if (liveSmb)
        {
            using var program = await MacroProgram.LoadForLaunchAsync(source, true, file => Task.FromResult(file == remote));
            var key = MacroTrustKey.Create(program!.SourceDigest!, commandLine.Arguments,
                MacroTrustKey.ConstantsVersion(program.Constants));
            MacroTrust.Save(key, MacroTrustDecision.Allow);
            try
            {
                Task request = window.OpenExternalMacroAsync(commandLine);
                await DecideMacroRequest(app, window, remote, null);
                await request.WaitAsync(TimeSpan.FromSeconds(8));
                CheckMacroLaunch(window.TerminalTabs.Count() == tabs, "A remembered allow bypassed read consent.");
                var priorTabs = window.TerminalTabs.ToHashSet();
                request = window.OpenExternalMacroAsync(commandLine);
                await DecideMacroRequest(app, window, remote, null, "MacroReadSource");
                await request.WaitAsync(TimeSpan.FromSeconds(8)); // No new execution review after consent.
                var run = window.TerminalTabs.Single(tab => !priorTabs.Contains(tab)).Macro!;
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(8));
                CheckMacroLaunch(run.Status.State == MacroRunState.Completed, "A consented remembered run failed.");
            }
            finally { MacroTrust.Forget(key.Fingerprint); }
        }
        if (protectedPath is null)
            Console.WriteLine(liveSmb ? "PASS SharedConstantsLiveSmbReadPermission" : "PASS SharedConstantsRemoteCancellation");

        async Task CheckRoute(Func<Task> launch, bool picked, string arguments)
        {
            Task request = launch();
            await DecideMacroRequest(app, window, remote, null);
            await request.WaitAsync(TimeSpan.FromSeconds(8));
            CheckMacroLaunch(window.TerminalTabs.Count() == tabs,
                "Declining the dependency read started a macro.");
            if (!liveSmb) return;

            var priorTabs = window.TerminalTabs.ToHashSet();
            request = launch();
            await DecideMacroRequest(app, window, remote, null, "MacroReadSource");
            if (picked) await AcceptFixtureMacroArguments(app, window);
            await DecideMacroRequest(app, window, source, arguments, "MacroRunOnce");
            await request.WaitAsync(TimeSpan.FromSeconds(8));
            var run = window.TerminalTabs.Single(tab => !priorTabs.Contains(tab)).Macro!;
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(8));
            CheckMacroLaunch(run.Status.State == MacroRunState.Completed
                && window.TerminalTabs.Count() == ++tabs, "The consented dependency did not supply the macro values.");
        }

    }

    private static async Task AcceptFixtureMacroArguments(App app, MainWindow window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Window? dialog;
        while ((dialog = app.Windows.OfType<Window>().SingleOrDefault(candidate =>
            candidate.Owner == window && candidate.IsVisible && candidate.Title == App.Text("MacroArguments"))) is null)
            await Task.Delay(10, timeout.Token);
        FindControls<Button>(dialog).Single(button => Equals(button.Content, App.Text("RunMacro")))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
