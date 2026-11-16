using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private WindowState macroRestoreState = WindowState.Normal;

    internal void RestoreMacroHost()
    {
        if (WindowState == WindowState.Minimized) WindowState = macroRestoreState;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized) macroRestoreState = WindowState;
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (TerminalTab terminal in TerminalTabs) terminal.DisposeMacro();
        base.OnClosed(e);
    }

    private async Task ChooseMacroAsync()
    {
        var picker = new OpenFileDialog { Filter = Text("MacroFiles"), DefaultExt = ".tttym" };
        if (app.Modal(() => picker.ShowDialog(this)) != true) return;
        await OpenMacroAsync(picker.FileName, null);
    }

    internal async Task OpenMacroAsync(string path, string[]? args)
    {
        if (closing) return;
        try
        {
            MacroProgram program = await Task.Run(() => MacroProgram.LoadAsync(path));
            if (!CanAcceptTabs) return;
            args ??= ReadMacroArguments();
            if (args is null || !CanAcceptTabs) return;
            var terminal = new TerminalTab(program.FullPath);
            terminal.PrepareMacro(program, args);
            try
            {
                AddTerminalTab(terminal);
                terminal.Macro!.Start();
            }
            catch
            {
                terminal.DisposeMacro();
                throw;
            }
        }
        catch (MacroSourceException error)
        {
            if (IsLoaded) ShowMacroLaunchError(string.Format(Text("MacroLocation"), error.Position.Line, error.Position.Column,
                TerminalTab.MacroErrorLabel(error.Code)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (IsLoaded) ShowMacroLaunchError(Text("MacroReadFailed"));
        }
    }

    private void ShowMacroLaunchError(string detail) =>
        app.Modal(() => ThemedMessageBox.Show(this, detail, Text("RunMacro"), icon: MessageBoxImage.Error));

    private string[]? ReadMacroArguments()
    {
        var form = new FormWindow(this, Text("RunMacro")) { Width = 560 };
        var rows = new StackPanel();
        var fields = new List<TextBox>();
        form.AddField("MacroArguments", new ScrollViewer
        {
            Content = rows, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        form.AddAction("MacroAddArgument", () =>
        {
            if (fields.Count == 64) return;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var input = new TextBox { MaxLength = 4096 };
            AutomationProperties.SetName(input, Text("MacroArgument"));
            var remove = new Button { Content = Text("MacroRemoveArgument"), Margin = new Thickness(4, 0, 0, 0) };
            remove.Click += (_, _) => { fields.Remove(input); rows.Children.Remove(row); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(input);
            fields.Add(input);
            rows.Children.Add(row);
            input.Focus();
        });
        form.AddButtons(() => { }, acceptKey: "RunMacro");
        return app.Modal(form.ShowDialog) == true ? fields.Select(input => input.Text).ToArray() : null;
    }
}
