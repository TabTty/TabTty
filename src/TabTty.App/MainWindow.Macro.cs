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

    internal async Task RunMacroAsync(string path, string[]? args)
    {
        if (!CanAcceptTabs) return;
        try
        {
            using var program = await Task.Run(() => MacroProgram.LoadForLaunchAsync(path, args is null,
                file => Dispatcher.InvokeAsync(() => CanAcceptTabs && ConfirmMacroRead(file)).Task));
            if (program is null || !CanAcceptTabs) return;
            args ??= ReadMacroArguments(this, "RunMacro");
            if (args is null || !CanAcceptTabs) return;
            if (!ApproveMacro(program, args) || !CanAcceptTabs) return;
            var terminal = new TerminalTab(program.FullPath);
            try
            {
                terminal.PrepareMacro(program, args);
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
            if (IsLoaded)
            {
                string detail = TerminalTab.MacroErrorLabel(error.Code);
                if (error.Position.Line > 0)
                    detail = string.Format(Text("MacroLocation"), error.Position.Line, error.Position.Column, detail);
                ShowMacroLaunchError(PermissionDisplay.Literal(error.FilePath ?? path) + Environment.NewLine + detail);
            }
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
        review.AddField("MacroReadFilePath", ReadOnlyMacroValue(PermissionDisplay.Literal(path)));
        review.AddButtons(() => { }, acceptKey: "MacroReadSource", cancelDefault: true);
        return app.Modal(review.ShowDialog) == true && CanAcceptTabs;
    }

    private bool ApproveMacro(MacroProgram program, string[] arguments)
    {
        var key = MacroTrustKey.Create(program.SourceDigest!, arguments, MacroTrustKey.ConstantsVersion(program.Constants));
        try
        {
            MacroTrustDecision? remembered = MacroTrust.Read(key);
            if (remembered == MacroTrustDecision.Block) return false;
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var token = AiPipe.ReadTokenIdentity(identity.AccessToken);
            bool elevated = token.Elevated ||
                int.Parse(token.IntegritySid.AsSpan("S-1-16-".Length), CultureInfo.InvariantCulture) >= 0x3000;
            if (remembered == MacroTrustDecision.Allow && !elevated) return true;

            using var pending = MacroTrust.BeginReview(key);
            // Bound escaped metadata, input/control copies and the active inspection page.
            long reviewUnits = program.FullPath.Length + arguments.Sum(value => (long)value.Length + 16) +
                program.Constants.Sum(binding => (long)binding.Alias.Length + binding.File.FullPath.Length + 64);
            int largestText = program.Constants.Aggregate(program.Source.Length, (length, binding) => Math.Max(length, binding.File.Source.Length));
            using var reviewMemory = program.ReserveReview(16_384 + 96 * (reviewUnits + Math.Min(largestText, MacroReviewPageUnits + 1)));
            var review = new FormWindow(this, Text("MacroReview")) { Width = 600 };
            review.AddPinnedNotice(Text(elevated ? "MacroElevatedNotice" : "MacroTrustNotice"));
            review.AddField("MacroSourceFile", ReadOnlyMacroValue(PermissionDisplay.Literal(program.FullPath)));
            review.AddField("MacroSourceDigest", ReadOnlyMacroValue(key.SourceDigest));
            string values = arguments.Length == 0 ? Text("MacroNoArguments") : string.Join("\n",
                arguments.Select((value, index) => $"[{index}] \"{PermissionDisplay.Literal(value)}\""));
            review.AddField("MacroArguments", ReadOnlyMacroValue(values));
            review.AddAction("MacroInspectSource", () => InspectMacroText(review, "MacroInspectSource", program.Source));
            if (key.ConstantsDigest is { } constantsDigest)
            {
                review.AddField("MacroConstantsVersion", ReadOnlyMacroValue(constantsDigest));
                var aliases = new ComboBox { ItemsSource = program.Constants.Select(binding => binding.Alias).ToArray() };
                var path = ReadOnlyMacroValue("");
                var fileDigest = ReadOnlyMacroValue("");
                aliases.SelectionChanged += (_, _) =>
                {
                    if (aliases.SelectedIndex < 0) return;
                    var file = program.Constants[aliases.SelectedIndex].File;
                    path.Text = PermissionDisplay.Literal(file.FullPath);
                    fileDigest.Text = file.Digest;
                };
                aliases.SelectedIndex = 0;
                review.AddField("MacroConstantsAlias", aliases);
                review.AddField("MacroReadFilePath", path);
                review.AddField("MacroConstantsFileDigest", fileDigest);
                review.AddAction("MacroInspectConstants", () =>
                    InspectMacroText(review, "MacroInspectConstants", program.Constants[aliases.SelectedIndex].File.Source));
            }
            MacroTrustDecision? choice = null;
            if (remembered is null)
            {
                if (!elevated) review.AddAction("MacroAlwaysAllow", () => Select(MacroTrustDecision.Allow));
                review.AddAction("MacroAlwaysBlock", () => Select(MacroTrustDecision.Block));
            }
            review.AddButtons(() => { }, acceptKey: "MacroRunOnce", cancelDefault: true);
            if (app.Modal(review.ShowDialog) != true || !CanAcceptTabs) return false;
            if (pending.Expired || MacroTrust.Read(key) != remembered)
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
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException)
        {
            if (IsLoaded) ShowMacroLaunchError(Text("MacroTrustUnavailable"));
            return false;
        }
    }

    private const int MacroReviewPageUnits = 8192;

    private void InspectMacroText(Window owner, string titleKey, string text)
    {
        var dialog = new FormWindow(owner, Text(titleKey)) { Width = 780 };
        var contents = ReadOnlyMacroValue("");
        contents.MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 240);
        dialog.AddField(titleKey, contents);
        var position = ReadOnlyMacroValue("");
        int page = 0;
        int pages = Math.Max(1, (text.Length + MacroReviewPageUnits - 1) / MacroReviewPageUnits);
        if (pages > 1)
        {
            dialog.AddField("PastePreviewPageLabel", position);
            dialog.AddAction("PastePreviousPage", () => { if (page > 0) ShowPage(page - 1); });
            dialog.AddAction("PasteNextPage", () => { if (page + 1 < pages) ShowPage(page + 1); });
        }
        ShowPage(0);
        dialog.AddButtons(() => { }, acceptKey: "OK");
        app.Modal(dialog.ShowDialog);

        void ShowPage(int index)
        {
            page = index;
            int start = Boundary(page);
            contents.Text = PermissionDisplay.PastePage(text.AsSpan(start, Boundary(page + 1) - start));
            position.Text = string.Format(CultureInfo.CurrentCulture, Text("PastePreviewPage"), page + 1, pages);
        }

        int Boundary(int index)
        {
            int offset = index * MacroReviewPageUnits;
            if (offset >= text.Length) return text.Length;
            if (offset > 0 && (char.IsLowSurrogate(text[offset]) || text[offset] == '\n' && text[offset - 1] == '\r')) return offset - 1;
            return offset;
        }
    }

    private static TextBox ReadOnlyMacroValue(string value) => new()
    {
        Text = value, IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private void ResetMacroTrust(Window owner)
    {
        string message = "MacroTrustResetConfirm", action = "MacroTrustReset";
        while (true)
        {
            bool confirmed = false;
            var dialog = ThemedMessageBox.Create(owner, Text(message), Text("MacroTrustReset"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel,
                result => confirmed = result == MessageBoxResult.OK,
                result => Text(result == MessageBoxResult.OK ? action : "Cancel"));
            app.Modal(dialog.ShowDialog);
            if (!confirmed) return;
            try { MacroTrust.Reset(); return; }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
            {
                message = "MacroTrustResetFailed";
                action = "MacroTrustResetRetry";
            }
        }
    }

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
                void DisplayEntries(IEnumerable<MacroTrust.Entry> selectedEntries)
                {
                    entries = selectedEntries.OrderBy(entry => entry.Fingerprint, StringComparer.Ordinal).ToArray();
                    pairs.Items.Clear();
                    foreach (var entry in entries)
                    {
                        string status = Text(entry.Decision switch
                        {
                            MacroTrustDecision.Allow => "MacroTrustAllowed",
                            MacroTrustDecision.Block => "MacroTrustBlocked",
                            _ => "MacroTrustInvalid",
                        });
                        string label = $"{entry.Fingerprint}  {status}";
                        if (entry.ConstantsDigest is { } version) label += $"\n{Text("MacroConstantsVersion")}  {version}";
                        pairs.Items.Add(label);
                    }
                    pairs.SelectedIndex = entries.Length == 0 ? -1 : 0;
                }
                sources.SelectionChanged += (_, _) => DisplayEntries(groups[sources.SelectedIndex]);
                sources.SelectedIndex = 0;
                dialog.AddAction("MacroFindArguments", () =>
                {
                    if (groups[sources.SelectedIndex].Key is not { } digest) return;
                    string[]? arguments = ReadMacroArguments(dialog, "MacroFindArguments");
                    if (arguments is null) return;
                    try
                    {
                        var matches = groups[sources.SelectedIndex].Where(entry =>
                            MacroTrustKey.Create(digest, arguments, entry.ConstantsDigest).Fingerprint == entry.Fingerprint);
                        DisplayEntries(matches);
                        if (entries.Length == 0)
                            app.Modal(() => ThemedMessageBox.Show(dialog, Text("MacroTrustNoMatch"), dialog.Title));
                    }
                    catch (MacroSourceException error)
                    {
                        app.Modal(() => ThemedMessageBox.Show(dialog, TerminalTab.MacroErrorLabel(error.Code), dialog.Title,
                            icon: MessageBoxImage.Error));
                    }
                });
                dialog.AddAction("MacroShowAllDecisions", () => DisplayEntries(groups[sources.SelectedIndex]));
                dialog.AddAsyncButtons(() =>
                {
                    if (pairs.SelectedIndex < 0) return Task.FromResult(false);
                    try
                    {
                        var entry = entries[pairs.SelectedIndex];
                        MacroTrust.Forget(entry.Fingerprint, entry.Version);
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
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var input = new TextBox();
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
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        rows.Children.Add(error);
        string[]? accepted = null;
        form.AddAsyncButtons(() =>
        {
            var arguments = fields.Select(input => input.Text).ToArray();
            if (MacroLimits.CheckArguments(arguments) is { } problem)
            {
                error.Text = string.Format(Text(problem.Code), problem.Excess, problem.Row);
                error.Visibility = Visibility.Visible;
                error.BringIntoView();
                return Task.FromResult(false);
            }
            accepted = arguments;
            return Task.FromResult(true);
        }, acceptKey: acceptKey);
        return app.Modal(form.ShowDialog) == true ? accepted : null;
    }
}
