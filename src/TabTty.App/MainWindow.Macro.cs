using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using TabTty.AiTransport;
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
        await RunMacroAsync(picker.FileName, null);
    }

    internal Task OpenExternalMacroAsync(InstanceLaunch launch) => RunMacroAsync(launch.Path, [.. launch.Arguments]);

    private async Task RunMacroAsync(string path, string[]? args)
    {
        if (!CanAcceptTabs) return;
        try
        {
            path = MacroProgram.ResolveSourcePath(path);
            MacroProgram? program = null;
            if (args is not null)
            {
                program = await Task.Run(() => MacroProgram.TryLoadLocalAsync(path));
                if (!CanAcceptTabs || program is null && !ConfirmMacroRead(path)) return;
            }
            program ??= await Task.Run(() => MacroProgram.LoadAsync(path));
            if (!CanAcceptTabs) return;
            args ??= ReadMacroArguments(this, "RunMacro");
            if (args is null || !CanAcceptTabs) return;
            if (!ApproveMacro(program, args) || !CanAcceptTabs) return;
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

    private bool ConfirmMacroRead(string path)
    {
        var review = new FormWindow(this, Text("MacroReadRemote")) { Width = 560 };
        review.AddPinnedNotice(Text("MacroReadRemoteNotice"));
        review.AddField("MacroSourceFile", ReadOnlyMacroValue(PermissionDisplay.Literal(path)));
        review.AddButtons(() => { }, acceptKey: "MacroReadSource", cancelDefault: true);
        return app.Modal(review.ShowDialog) == true && CanAcceptTabs;
    }

    private bool ApproveMacro(MacroProgram program, string[] arguments)
    {
        var key = MacroTrustKey.Create(program.SourceDigest!, arguments);
        try
        {
            MacroTrustDecision? remembered = MacroTrust.Read(key);
            if (remembered == MacroTrustDecision.Block) return false;
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var token = AiPipe.ReadTokenIdentity(identity.AccessToken);
            bool elevated = token.Elevated ||
                int.Parse(token.IntegritySid.AsSpan("S-1-16-".Length), CultureInfo.InvariantCulture) >= 0x3000;
            if (remembered == MacroTrustDecision.Allow && !elevated) return true;

            long revision = MacroTrust.Revision;
            var review = new FormWindow(this, Text("MacroReview")) { Width = 600 };
            review.AddPinnedNotice(Text(elevated ? "MacroElevatedNotice" : "MacroTrustNotice"));
            review.AddField("MacroSourceFile", ReadOnlyMacroValue(PermissionDisplay.Literal(program.FullPath)));
            review.AddField("MacroSourceDigest", ReadOnlyMacroValue(key.SourceDigest));
            string values = arguments.Length == 0 ? Text("MacroNoArguments") : string.Join("\n",
                arguments.Select((value, index) => $"[{index}] \"{PermissionDisplay.Literal(value)}\""));
            review.AddField("MacroArguments", ReadOnlyMacroValue(values));
            review.AddAction("MacroInspectSource", () =>
            {
                var source = new FormWindow(review, Text("MacroInspectSource")) { Width = 780 };
                var contents = ReadOnlyMacroValue(PermissionDisplay.PastePage(program.Source));
                contents.MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 220);
                source.AddField("MacroInspectSource", contents);
                source.AddButtons(() => { }, acceptKey: "OK");
                app.Modal(source.ShowDialog);
            });
            MacroTrustDecision? choice = null;
            if (remembered is null)
            {
                if (!elevated) review.AddAction("MacroAlwaysAllow", () => Select(MacroTrustDecision.Allow));
                review.AddAction("MacroAlwaysBlock", () => Select(MacroTrustDecision.Block));
            }
            review.AddButtons(() => { }, acceptKey: "MacroRunOnce", cancelDefault: true);
            if (app.Modal(review.ShowDialog) != true || !CanAcceptTabs) return false;
            if (revision != MacroTrust.Revision || MacroTrust.Read(key) != remembered)
            {
                ShowMacroLaunchError(Text("MacroTrustChanged"));
                return false;
            }
            if (choice is { } decision) MacroTrust.Save(key, decision);
            return choice != MacroTrustDecision.Block;

            void Select(MacroTrustDecision decision)
            {
                choice = decision;
                review.DialogResult = true;
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            if (IsLoaded) ShowMacroLaunchError(Text("MacroTrustUnavailable"));
            return false;
        }
    }

    private static TextBox ReadOnlyMacroValue(string value) => new()
    {
        Text = value, IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private void ShowMacroLaunchError(string detail) =>
        app.Modal(() => ThemedMessageBox.Show(this, detail, Text("RunMacro"), icon: MessageBoxImage.Error));

    private void ManageMacroTrust()
    {
        try
        {
            var groups = MacroTrust.List().GroupBy(entry => entry.SourceDigest)
                .OrderBy(group => group.Key is null).ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
            var dialog = new FormWindow(this, Text("MacroManageTrust")) { Width = 650 };
            if (groups.Length == 0)
            {
                dialog.AddNotice("MacroTrustEmpty");
                dialog.AddButtons(() => { }, acceptKey: "OK");
            }
            else
            {
                var sources = new ComboBox();
                var pairs = new ComboBox();
                MacroTrust.Entry[] entries = [];
                dialog.AddField("MacroSourceDigest", sources);
                dialog.AddField("MacroPairFingerprint", pairs);
                foreach (var group in groups) sources.Items.Add(group.Key ?? Text("MacroTrustInvalid"));
                sources.SelectionChanged += (_, _) =>
                {
                    entries = groups[sources.SelectedIndex].OrderBy(entry => entry.Fingerprint, StringComparer.Ordinal).ToArray();
                    pairs.Items.Clear();
                    foreach (var entry in entries)
                    {
                        string status = Text(entry.Decision switch
                        {
                            MacroTrustDecision.Allow => "MacroTrustAllowed",
                            MacroTrustDecision.Block => "MacroTrustBlocked",
                            _ => "MacroTrustInvalid",
                        });
                        pairs.Items.Add($"{entry.Fingerprint}  {status}");
                    }
                    pairs.SelectedIndex = 0;
                };
                sources.SelectedIndex = 0;
                dialog.AddAction("MacroFindArguments", () =>
                {
                    if (groups[sources.SelectedIndex].Key is not { } digest) return;
                    string[]? arguments = ReadMacroArguments(dialog, "MacroFindArguments");
                    if (arguments is null) return;
                    try
                    {
                        var key = MacroTrustKey.Create(digest, arguments);
                        int index = Array.FindIndex(entries, entry => entry.Fingerprint == key.Fingerprint);
                        if (index >= 0) pairs.SelectedIndex = index;
                        else app.Modal(() => ThemedMessageBox.Show(dialog, Text("MacroTrustNoMatch"), dialog.Title));
                    }
                    catch (MacroSourceException error)
                    {
                        app.Modal(() => ThemedMessageBox.Show(dialog, TerminalTab.MacroErrorLabel(error.Code), dialog.Title,
                            icon: MessageBoxImage.Error));
                    }
                });
                dialog.AddAsyncButtons(() =>
                {
                    try
                    {
                        MacroTrust.Forget(entries[pairs.SelectedIndex].Fingerprint);
                        return Task.FromResult(true);
                    }
                    catch (Win32Exception)
                    {
                        app.Modal(() => ThemedMessageBox.Show(dialog, Text("MacroTrustUnavailable"), dialog.Title,
                            icon: MessageBoxImage.Error));
                        return Task.FromResult(false);
                    }
                }, acceptKey: "MacroForgetDecision", cancelDefault: true);
            }
            app.Modal(dialog.ShowDialog);
        }
        catch (Win32Exception)
        {
            app.Modal(() => ThemedMessageBox.Show(this, Text("MacroTrustUnavailable"), Text("MacroManageTrust"),
                icon: MessageBoxImage.Error));
        }
    }

    private string[]? ReadMacroArguments(Window owner, string acceptKey)
    {
        var form = new FormWindow(owner, Text("MacroArguments")) { Width = 560 };
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
        form.AddButtons(() => { }, acceptKey: acceptKey);
        return app.Modal(form.ShowDialog) == true ? fields.Select(input => input.Text).ToArray() : null;
    }
}
