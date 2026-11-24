using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal static class PostConnectionRoutineMenu
{
    private enum Routine { Commands, Sudo, Enable }
    private static readonly JsonSerializerOptions LiteralOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static Button Create(FormView form, TextBox source, App app, Func<bool> profilePasswordAvailable)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.IsOpen = true;
        form.AddLocalizedUpdate(() => button.Content = Text("PostConnectionInsertRoutine") + " ▾");
        Add(Routine.Commands, "PostConnectionRoutineCommands");
        Add(Routine.Sudo, "PostConnectionRoutineSudo");
        Add(Routine.Enable, "PostConnectionRoutineEnable");
        return button;

        void Add(Routine routine, string label)
        {
            var item = new MenuItem();
            form.AddLocalizedUpdate(() => item.Header = Text(label));
            item.Click += (_, _) => Open(form.OwnerWindow, source, app, routine, label, profilePasswordAvailable());
            menu.Items.Add(item);
        }
    }

    private static void Open(Window owner, TextBox source, App app, Routine routine, string label, bool useProfilePassword)
    {
        int caret = source.CaretIndex;
        var dialog = new MainWindow.FormWindow(owner, Text(label)) { Width = 480 };
        var waitFirst = new CheckBox { IsChecked = true };
        dialog.AddField("PostConnectionRoutineWaitFirst", waitFirst, inlineCheckBox: true);
        dialog.AddNotice("PostConnectionRoutineWaitFirstHelp");
        var normal = Prompt("PostConnectionRoutineNormalPrompt");
        if (routine != Routine.Commands)
        {
            waitFirst.Checked += (_, _) => normal.IsEnabled = true;
            waitFirst.Unchecked += (_, _) => normal.IsEnabled = false;
        }
        var commands = new TextBox { AcceptsReturn = true, MinLines = 4, MaxLines = 8, MaxLength = 8192,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBox? privileged = null, challenge = null, entry = null;
        CheckBox? profilePassword = null;
        if (routine == Routine.Commands)
            dialog.AddField("PostConnectionRoutineCommandLines", commands);
        else
        {
            privileged = Prompt("PostConnectionRoutinePrivilegedPrompt");
            challenge = Prompt("PostConnectionRoutinePasswordPrompt", routine == Routine.Sudo ? "[sudo] password" : "Password:");
            if (useProfilePassword)
            {
                profilePassword = new CheckBox { IsChecked = true };
                dialog.AddField("PostConnectionRoutineUseProfilePassword", profilePassword, inlineCheckBox: true);
            }
            entry = Prompt("PostConnectionRoutineVaultEntry");
            if (profilePassword is not null)
            {
                entry.IsEnabled = false;
                profilePassword.Checked += (_, _) => entry.IsEnabled = false;
                profilePassword.Unchecked += (_, _) => entry.IsEnabled = true;
            }
        }

        string insertion = "";
        dialog.AddButtons(() =>
        {
            bool waitInitial = waitFirst.IsChecked == true;
            if ((waitInitial || routine == Routine.Commands) && string.IsNullOrWhiteSpace(normal.Text))
                throw new ArgumentException(Text("PostConnectionRoutinePromptRequired"));
            insertion = routine == Routine.Commands
                ? BuildCommands(commands.Text, normal.Text, waitInitial)
                : BuildPrivilege(routine == Routine.Sudo ? "sudo -i" : "enable", normal.Text, privileged!.Text,
                    challenge!.Text, profilePassword?.IsChecked == true ? null : entry!.Text, waitInitial);
        }, acceptKey: "PostConnectionRoutineInsert");
        if (app.Modal(dialog.ShowDialog) != true) return;

        string newline = Environment.NewLine;
        if (caret > 0 && source.Text[caret - 1] is not ('\r' or '\n')) insertion = newline + insertion;
        if (caret == source.Text.Length || source.Text[caret] is not ('\r' or '\n')) insertion += newline;
        source.Select(caret, 0);
        source.SelectedText = insertion;
        source.CaretIndex = caret + insertion.Length;
        source.Focus();

        TextBox Prompt(string key, string value = "")
        {
            var box = new TextBox { Text = value, MaxLength = 512 };
            dialog.AddField(key, box);
            return box;
        }
    }

    private static string BuildCommands(string commands, string prompt, bool waitInitial)
    {
        var code = new StringBuilder();
        using var lines = new StringReader(commands);
        string wait = Wait(prompt);
        while (lines.ReadLine() is { } command)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            if (waitInitial || code.Length != 0) code.AppendLine(wait);
            code.AppendLine(Send(command));
        }
        if (code.Length == 0) throw new ArgumentException(Text("PostConnectionRoutineCommandRequired"));
        return code.ToString().TrimEnd('\r', '\n');
    }

    private static string BuildPrivilege(string command, string normal, string privileged, string challenge,
        string? entry, bool waitInitial)
    {
        if (string.IsNullOrWhiteSpace(privileged) || string.IsNullOrWhiteSpace(challenge))
            throw new ArgumentException(Text("PostConnectionRoutinePromptRequired"));
        if (entry is not null && !VaultPayload.IsValidName(entry))
            throw new ArgumentException(Text("PostConnectionRoutineVaultEntryRequired"));
        var code = new StringBuilder();
        if (waitInitial) code.AppendLine(Wait(normal));
        code.AppendLine(Send(command));
        code.AppendLine($"if (WaitAnyPrompt([password: Literal({Quote(challenge)}), ready: Literal({Quote(privileged)})], 10000).AlternativeName == \"password\")");
        code.AppendLine("{");
        code.AppendLine($"    WritePasswordLine({(entry is null ? "" : Quote(entry))});");
        code.AppendLine($"    {Wait(privileged)}");
        return code.Append('}').ToString();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, LiteralOptions);
    private static string Wait(string prompt) => $"WaitPrompt(Literal({Quote(prompt)}), 10000);";
    private static string Send(string command) => $"WriteLine({Quote(command)}, markReceiveBoundary: true);";
}
