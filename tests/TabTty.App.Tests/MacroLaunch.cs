using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task MacroLaunchAndDialog(App app, string directory)
    {
        string source = Path.Combine(directory, "dialog.tttym");
        await File.WriteAllTextAsync(source, """
            #tttym 1
            var answer = Dialog(Macro.BaseName, Macro.Args[0], DialogButtons.OKCancel, true, "");
            if (answer.Button != DialogButton.OK || !answer.HasText || answer.Text != "") {
                var fail = 1 / 0;
            }
            """);
        var launch = InstanceLaunch.Parse(["--macro", source, "--", "A B", ""]).Single();
        if (!launch.Arguments.SequenceEqual(["A B", ""])) throw new InvalidOperationException("Macro argument boundaries changed.");
        var window = app.CreateWindow(restorePlacement: false);
        window.Show();
        try
        {
            await window.OpenMacroAsync(launch.Path, launch.Arguments);
            TerminalTab tab = window.TerminalTabs.Single();
            MacroRun run = tab.Macro!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            MacroDialogWindow? prompt = null;
            while ((prompt = app.Windows.OfType<MacroDialogWindow>().SingleOrDefault()) is null)
                await Task.Delay(10, timeout.Token);
            if (!ReferenceEquals(prompt.Owner, window) || !window.IsEnabled)
                throw new InvalidOperationException("The macro dialog disabled or lost its host.");
            if (!FindControls<TextBlock>(prompt).Any(block => block.Text == "A B"))
                throw new InvalidOperationException("The macro did not receive the launch argument.");
            var ok = FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("OK")));
            var cancel = FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("Cancel")));
            if (!cancel.IsDefault) throw new InvalidOperationException("Cancel was not the default.");
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await run.Completion.WaitAsync(timeout.Token);
            if (run.Status.State != MacroRunState.Completed || !window.TerminalTabs.Contains(tab))
                throw new InvalidOperationException("Accepted empty input failed or the preconnection result tab disappeared.");
        }
        finally
        {
            foreach (TerminalTab tab in window.TerminalTabs) tab.Macro?.Stop();
            foreach (TerminalTab tab in window.TerminalTabs) await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
        }
    }
}
