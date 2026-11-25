using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed record HighlightSet(Guid Id, string Name, bool Enabled, IReadOnlyList<TerminalHighlightRule> Rules);

internal sealed class HighlightCatalog(App app, string pathName, Action<string, string, string?>? issue)
{
    private IReadOnlyList<HighlightSet>? sets;
    private byte[]? original;
    private JsonObject? originalDocument;
    private bool temporaryDefaults;
    public event Action? Changed;

    public bool Initialize()
    {
        try { _ = GetSets(); return true; }
        catch (Exception exception) when (InvalidStore(exception))
        {
            if (ThemedMessageBox.Show(Text("UseTemporaryHighlighting"), "TabTty", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            temporaryDefaults = true;
            sets = [];
            return true;
        }
    }

    public JsonStoreRecovery.Store RecoveryStore() => new(Text("HighlightSets"), pathName,
        path => _ = ReadSets(ReadBytes(path), out _), () =>
        {
            sets = null;
            original = null;
            originalDocument = null;
            temporaryDefaults = false;
            _ = GetSets();
            Changed?.Invoke();
        }, () => "{\"schemaVersion\":1,\"requiredFeatures\":[],\"sets\":[]}"u8.ToArray());

    public HighlightSet? Find(Guid? id)
    {
        if (id is null) return null;
        try { return GetSets().FirstOrDefault(set => set.Id == id.Value); }
        catch (Exception exception) when (InvalidStore(exception)) { return null; }
    }

    public Func<Guid?> AddPicker(FormView form, Guid? selected = null)
    {
        IReadOnlyList<HighlightSet> available;
        try { available = GetSets(); }
        catch (Exception exception) when (InvalidStore(exception)) { available = []; }
        var options = new List<HighlightChoice> { new(null, Text("NoHighlighting")) };
        options.AddRange(available.Select(set => new HighlightChoice(set.Id, set.Name)));
        if (selected is not null && !options.Any(option => option.Id == selected))
            options.Add(new HighlightChoice(selected, $"{Text("MissingHighlightSet")} ({selected:D})"));
        var selection = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(HighlightChoice.Name),
            SelectedItem = options.First(option => option.Id == selected) };
        form.AddField("HighlightSet", selection);
        return () => (selection.SelectedItem as HighlightChoice)?.Id;
    }

    public void Show(MainWindow owner)
    {
        try
        {
            owner.OpenForm("HighlightSets", Build);
        }
        catch (Exception exception) when (InvalidStore(exception))
        {
            MainWindow.ShowError(owner, new InvalidDataException(Text("HighlightStoreInvalid")));
        }

        void Build(FormView form)
        {
            form.ShowFileOperationStatus();
            var originalSets = GetSets();
            var draft = originalSets.ToList();
            var list = new ListBox { DisplayMemberPath = nameof(HighlightSet.Name), Height = 250 };
            var rules = new ListBox { DisplayMemberPath = nameof(TerminalHighlightRule.Name), Height = 208 };
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            var selectedActions = new List<Button>();
            form.AddActionRow(() => form.AddAction("AddHighlightSet", () =>
            {
                if (draft.Count == 1000) return;
                if (EditSet(form.OwnerWindow, new HighlightSet(Guid.NewGuid(), "", true, [])) is not { } updated) return;
                draft.Add(updated);
                Refresh(list, draft, draft.Count - 1);
            }));
            form.AddColumns(() => form.AddGroup("HighlightSetList", () =>
            {
                form.AddDisplay("HighlightSetList", list);
            }), () => form.AddGroup("HighlightRules", () =>
            {
                form.AddDisplay("HighlightSet", summary);
                form.AddDisplay("HighlightRules", rules);
                form.AddActionRow(() =>
                {
                    selectedActions.Add(form.AddAction("EditHighlightSet", EditSelected));
                    selectedActions.Add(form.AddAction("DuplicateHighlightSet", DuplicateSelected));
                    selectedActions.Add(form.AddAction("RemoveHighlightSet", RemoveSelected));
                });
            }));
            list.SelectionChanged += (_, _) => UpdateSelection();
            form.AddLocalizedUpdate(UpdateSelection);
            Refresh(list, draft, 0);
            form.TrackValue(draft.ToArray, saved =>
            {
                draft.Clear();
                draft.AddRange(saved);
                Refresh(list, draft, list.SelectedIndex);
            }, (saved, current) => saved.Length == current.Length && saved.Zip(current).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
                pair.First.Enabled == pair.Second.Enabled && pair.First.Rules.SequenceEqual(pair.Second.Rules)));
            form.AddEditorButtons(async () =>
            {
                if (!ReferenceEquals(originalSets, GetSets()))
                    throw new InvalidOperationException(Text("ManagementTabOutdated"));
                await SaveAsync(draft.ToArray());
                originalSets = GetSets();
                return true;
            });

            void UpdateSelection()
            {
                var selected = list.SelectedItem as HighlightSet;
                foreach (Button action in selectedActions) action.IsEnabled = selected is not null;
                summary.Text = selected is null ? Text(draft.Count == 0 ? "HighlightSetsEmpty" : "SelectHighlightSet")
                    : $"{selected.Name} · {Text(selected.Enabled ? "Enabled" : "Off")}";
                rules.ItemsSource = selected?.Rules;
                rules.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
            }

            void EditSelected()
            {
                int index = list.SelectedIndex;
                if (index < 0 || EditSet(form.OwnerWindow, draft[index]) is not { } updated) return;
                draft[index] = updated;
                Refresh(list, draft, index);
            }

            void DuplicateSelected()
            {
                if (list.SelectedItem is not HighlightSet set || draft.Count == 1000) return;
                if (EditSet(form.OwnerWindow, set with { Id = Guid.NewGuid(),
                        Rules = set.Rules.Select(rule => rule with { Id = Guid.NewGuid() }).ToArray() }) is not { } updated)
                    return;
                draft.Add(updated);
                Refresh(list, draft, draft.Count - 1);
            }

            void RemoveSelected()
            {
                int index = list.SelectedIndex;
                if (index < 0) return;
                draft.RemoveAt(index);
                Refresh(list, draft, index);
            }
        }
    }

    private HighlightSet? EditSet(Window owner, HighlightSet originalSet)
    {
        var form = new MainWindow.FormWindow(owner, Text("HighlightSet")) { Width = 540 };
        var name = new TextBox { Text = originalSet.Name, MaxLength = 512 };
        var enabled = new CheckBox { IsChecked = originalSet.Enabled };
        var list = new ListBox { DisplayMemberPath = nameof(TerminalHighlightRule.Name), MinHeight = 120, MaxHeight = 280 };
        var draft = originalSet.Rules.ToList();
        Refresh(list, draft, 0);
        form.AddField("HighlightName", name);
        form.AddField("Enabled", enabled);
        form.AddField("HighlightRules", list);
        form.AddAction("AddHighlightRule", () =>
        {
            if (draft.Count == 100) return;
            var newRule = new TerminalHighlightRule(Guid.NewGuid(), "", "", true, false, default);
            if (EditRule(form, newRule) is not { } added) return;
            draft.Add(added);
            Refresh(list, draft, draft.Count - 1);
        });
        form.AddAction("EditHighlightRule", () =>
        {
            int index = list.SelectedIndex;
            if (index < 0 || EditRule(form, draft[index]) is not { } updated) return;
            draft[index] = updated;
            Refresh(list, draft, index);
        });
        form.AddAction("MoveRuleUp", () => Move(-1));
        form.AddAction("MoveRuleDown", () => Move(1));
        form.AddAction("RemoveHighlightRule", () =>
        {
            int index = list.SelectedIndex;
            if (index < 0) return;
            draft.RemoveAt(index);
            Refresh(list, draft, index);
        });
        HighlightSet? result = null;
        form.AddButtons(() =>
        {
            ValidateName(name.Text);
            result = originalSet with { Name = name.Text, Enabled = enabled.IsChecked == true, Rules = draft.ToArray() };
        });
        return app.Modal(form.ShowDialog) == true ? result : null;

        void Move(int delta)
        {
            int index = list.SelectedIndex;
            int next = index + delta;
            if (index < 0 || next < 0 || next >= draft.Count) return;
            (draft[index], draft[next]) = (draft[next], draft[index]);
            Refresh(list, draft, next);
        }
    }

    private TerminalHighlightRule? EditRule(Window owner, TerminalHighlightRule rule)
    {
        var form = new MainWindow.FormWindow(owner, Text("EditHighlightRule")) { Width = 500 };
        var name = new TextBox { Text = rule.Name, MaxLength = 512 };
        var pattern = new TextBox { Text = rule.Pattern, MaxLength = 4096 };
        var enabled = new CheckBox { IsChecked = rule.Enabled };
        var ignoreCase = new CheckBox { IsChecked = rule.IgnoreCase };
        var foreground = new TextBox { Text = FormatColor(rule.Style.Foreground) ?? "", MaxLength = 7 };
        var background = new TextBox { Text = FormatColor(rule.Style.Background) ?? "", MaxLength = 7 };
        var bold = new CheckBox { IsThreeState = true, IsChecked = rule.Style.Bold };
        var underline = new CheckBox { IsThreeState = true, IsChecked = rule.Style.Underline };
        form.AddField("HighlightName", name);
        form.AddField("HighlightPattern", pattern);
        form.AddField("Enabled", enabled);
        form.AddField("IgnoreCase", ignoreCase);
        form.AddField("HighlightForeground", foreground);
        form.AddField("HighlightBackground", background);
        form.AddField("HighlightBold", bold);
        form.AddField("HighlightUnderline", underline);
        TerminalHighlightRule? result = null;
        form.AddButtons(() =>
        {
            ValidateName(name.Text);
            var style = new TerminalHighlightStyle(ParseColor(foreground.Text), ParseColor(background.Text),
                bold.IsChecked, underline.IsChecked);
            var updated = rule with { Name = name.Text, Pattern = pattern.Text, Enabled = enabled.IsChecked == true,
                IgnoreCase = ignoreCase.IsChecked == true, Style = style };
            try { _ = new TerminalHighlightRules([updated]); }
            catch (ArgumentException) { throw new ArgumentException(Text("InvalidHighlightPattern")); }
            result = updated;
        });
        return app.Modal(form.ShowDialog) == true ? result : null;
    }

    private IReadOnlyList<HighlightSet> GetSets()
    {
        if (sets is not null) return sets;
        byte[]? bytes = JsonStoreRecovery.ReadBounded(pathName, "HighlightStoreInvalid", allowMissing: true);
        if (bytes is null) return sets = [];
        sets = ReadSets(bytes, out originalDocument);
        original = bytes;
        return sets;
    }

    private static IReadOnlyList<HighlightSet> ReadSets(byte[] bytes, out JsonObject document)
    {
        document = JsonStoreRecovery.ParseObject(bytes);
        if (document["schemaVersion"]?.GetValue<int>() != 1 ||
            document["requiredFeatures"] is not JsonArray { Count: 0 } ||
            document["sets"] is not JsonArray { Count: <= 1000 } saved)
            throw new InvalidDataException(Text("HighlightStoreInvalid"));
        var loaded = new List<HighlightSet>(saved.Count);
        var identifiers = new HashSet<Guid>();
        foreach (var node in saved)
        {
            if (node is not JsonObject item || item["name"]?.GetValue<string>() is not { } name ||
                item["enabled"]?.GetValue<bool>() is not { } enabled ||
                item["rules"] is not JsonArray { Count: <= 100 } definitions ||
                !Guid.TryParseExact(item["setId"]?.GetValue<string>(), "D", out Guid id) || id == Guid.Empty || !identifiers.Add(id))
                throw new InvalidDataException(Text("HighlightStoreInvalid"));
            ValidateName(name);
            var rules = definitions.Select(ReadRule).ToArray();
            _ = new TerminalHighlightRules(rules);
            loaded.Add(new HighlightSet(id, name, enabled, rules));
        }
        return loaded.ToArray();
    }

    private static TerminalHighlightRule ReadRule(JsonNode? value)
    {
        if (value is not JsonObject rule || rule["name"]?.GetValue<string>() is not { } name ||
            rule["pattern"]?.GetValue<string>() is not { } pattern ||
            rule["enabled"]?.GetValue<bool>() is not { } enabled || rule["ignoreCase"]?.GetValue<bool>() is not { } ignoreCase ||
            rule["style"] is not JsonObject style || !Guid.TryParseExact(rule["ruleId"]?.GetValue<string>(), "D", out Guid id))
            throw new InvalidDataException(Text("HighlightStoreInvalid"));
        ValidateName(name);
        return new TerminalHighlightRule(id, name, pattern, enabled, ignoreCase, new TerminalHighlightStyle(
            ParseColor(style["foreground"]?.GetValue<string>()), ParseColor(style["background"]?.GetValue<string>()),
            style["bold"]?.GetValue<bool>(), style["underline"]?.GetValue<bool>()));
    }

    private async Task SaveAsync(IReadOnlyList<HighlightSet> draft)
    {
        if (temporaryDefaults) throw new InvalidOperationException(Text("UseTemporaryHighlighting"));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            requiredFeatures = Array.Empty<string>(),
            sets = draft.Select(set => new
            {
                setId = set.Id, name = set.Name, enabled = set.Enabled,
                rules = set.Rules.Select(rule => new
                {
                    ruleId = rule.Id, name = rule.Name, pattern = rule.Pattern, enabled = rule.Enabled, ignoreCase = rule.IgnoreCase,
                    style = new { foreground = FormatColor(rule.Style.Foreground), background = FormatColor(rule.Style.Background),
                        bold = rule.Style.Bold, underline = rule.Style.Underline },
                }),
            }),
        });
        JsonObject document = JsonStoreRecovery.ParseObject(bytes);
        if (originalDocument is not null) JsonStoreRecovery.PreserveUnknown(document, originalDocument);
        bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException(Text("HighlightStoreInvalid"));
        var operation = JsonStoreRecovery.BeginSave(pathName, bytes, original,
            path => _ = ReadSets(ReadBytes(path), out _), issue);
        JsonStoreRecovery.RequireVerified(await operation.ObserveAsync());
        original = bytes;
        originalDocument = document;
        sets = draft;
        Changed?.Invoke();
    }

    private static byte[] ReadBytes(string path) => JsonStoreRecovery.ReadBounded(path, "HighlightStoreInvalid")!;

    private static bool InvalidStore(Exception exception) => exception is IOException or InvalidDataException
        or JsonException or ArgumentException or FormatException or InvalidOperationException or UnauthorizedAccessException;

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EnumerateRunes().Count() > 256 || name.Any(char.IsControl))
            throw new ArgumentException(Text("InvalidHighlightName"));
    }

    private static string? FormatColor(uint? color) => color is { } value ? $"#{value:X6}" : null;

    private static uint? ParseColor(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length != 7 || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint color))
            throw new ArgumentException(Text("InvalidHighlightColor"));
        return color;
    }

    private static void Refresh<T>(ListBox list, List<T> draft, int index)
    {
        list.ItemsSource = draft.ToArray();
        list.SelectedIndex = Math.Min(index, draft.Count - 1);
    }

    private sealed record HighlightChoice(Guid? Id, string Name);
}
