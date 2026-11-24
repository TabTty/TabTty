using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace TabTty.App;

internal enum ShortcutAction { NextTab, PreviousTab, Copy, Paste, Search }

internal sealed class KeyboardShortcuts
{
    private const ModifierKeys ControlShift = ModifierKeys.Control | ModifierKeys.Shift;
    internal const string SettingsKey = "keyboardShortcuts";

    internal static IReadOnlyList<(ShortcutAction Action, string Label, string Default)> Commands { get; } =
    [
        (ShortcutAction.NextTab, "NextTabShortcut", "Ctrl+Tab"),
        (ShortcutAction.PreviousTab, "PreviousTabShortcut", "Ctrl+Shift+Tab"),
        (ShortcutAction.Copy, "Copy", "Ctrl+Shift+C"),
        (ShortcutAction.Paste, "Paste", "Ctrl+Shift+V"),
        (ShortcutAction.Search, "Search", "Ctrl+Shift+F"),
    ];

    private static readonly Dictionary<string, (Key, ModifierKeys)> Gestures = MakeGestures();
    internal static IEnumerable<string> Options => Gestures.Keys;
    internal static KeyboardShortcuts Defaults { get; } = new(Commands.Select(command => command.Default));

    private readonly string?[] assignments;
    private readonly Dictionary<(Key, ModifierKeys), ShortcutAction> actions = [];

    internal KeyboardShortcuts(IEnumerable<string?> selections)
    {
        assignments = selections.ToArray();
        if (assignments.Length != Commands.Count) throw new InvalidDataException("Incomplete shortcut assignments.");
        foreach (var command in Commands)
        {
            if (this[command.Action] is not { } selection) continue;
            if (!Gestures.TryGetValue(selection, out var gesture))
                throw new InvalidDataException("Unknown keyboard shortcut.");
            if (!actions.TryAdd(gesture, command.Action))
                throw new InvalidDataException(App.Text("DuplicateKeyboardShortcut"));
        }
    }

    internal string? this[ShortcutAction action] => assignments[(int)action];

    internal ShortcutAction? Match(KeyEventArgs e) =>
        actions.TryGetValue((e.Key, e.KeyboardDevice.Modifiers), out var action) ? action : null;

    internal static KeyboardShortcuts Read(JsonObject settings)
    {
        if (settings.TryGetPropertyValue(SettingsKey, out var stored))
        {
            if (stored is not JsonObject shortcuts) throw new InvalidDataException("Invalid keyboard shortcuts.");
            return new(Commands.Select(command =>
                shortcuts.TryGetPropertyValue(command.Action.ToString(), out var choice)
                    ? choice?.GetValue<string>() : command.Default));
        }

        return Defaults;
    }

    private static Dictionary<string, (Key, ModifierKeys)> MakeGestures()
    {
        var gestures = new Dictionary<string, (Key, ModifierKeys)>(StringComparer.Ordinal)
        {
            ["Ctrl+Tab"] = (Key.Tab, ModifierKeys.Control),
            ["Ctrl+Shift+Tab"] = (Key.Tab, ControlShift),
            ["Ctrl+PageUp"] = (Key.PageUp, ModifierKeys.Control),
            ["Ctrl+PageDown"] = (Key.PageDown, ModifierKeys.Control),
        };
        for (Key key = Key.A; key <= Key.Z; key++)
            gestures.Add($"Ctrl+Shift+{key}", (key, ControlShift));
        return gestures;
    }
}
