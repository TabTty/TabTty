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

internal sealed partial class HighlightCatalog(App app, string pathName, Action<string, string, string?>? issue)
{
    private const int MaximumSets = 1000;
    internal const int MaximumRules = 100;
    private readonly SemaphoreSlim writes = new(1);
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
                MessageBoxImage.Warning, MessageBoxResult.No, acceptKey: "UseTemporaryDefaults") != MessageBoxResult.Yes) return false;
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
        }, () => "{\"schemaVersion\":1,\"requiredFeatures\":[],\"sets\":[]}"u8.ToArray(), WriteGate: writes);

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

    internal TerminalHighlightRule? EditRule(Window owner, TerminalHighlightRule rule)
    {
        var form = new MainWindow.FormWindow(owner, Text("HighlightRuleEditorTitle")) { Width = 500 };
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
        var showPatternError = form.AddValidation(pattern);
        pattern.TextChanged += (_, _) => RefreshPatternError();
        RefreshPatternError();
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

        void RefreshPatternError()
        {
            try
            {
                _ = TerminalHighlightRules.CreateExpression(pattern.Text, ignoreCase.IsChecked == true);
                showPatternError("");
            }
            catch (ArgumentException)
            {
                showPatternError(Text("InvalidHighlightPattern"));
            }
        }
    }

    internal async Task<HighlightSet> SaveEditorAsync(HighlightSet? baseline, Func<HighlightSet> readDraft)
    {
        await writes.WaitAsync();
        try
        {
            HighlightSet draft = readDraft();
            ValidateName(draft.Name);
            var current = GetSets().ToList();
            int index = current.FindIndex(set => set.Id == draft.Id);
            if (index >= 0 && current[index].Enabled == draft.Enabled && SameDefinition(current[index], draft))
                return current[index];

            if (baseline is null && index < 0)
            {
                if (current.Count >= MaximumSets) throw new InvalidOperationException(Text("HighlightSetLimit"));
                current.Add(draft);
            }
            else if (baseline is not null && index >= 0 && SameDefinition(current[index], baseline))
                current[index] = draft;
            else
                throw new InvalidOperationException(Text("ManagementTabOutdated"));

            await SaveAsync(current);
            return draft;
        }
        finally { writes.Release(); }
    }

    private static bool SameDefinition(HighlightSet first, HighlightSet second) =>
        first.Name == second.Name && first.Rules.SequenceEqual(second.Rules);

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
            document["sets"] is not JsonArray { Count: <= MaximumSets } saved)
            throw new InvalidDataException(Text("HighlightStoreInvalid"));
        var loaded = new List<HighlightSet>(saved.Count);
        var identifiers = new HashSet<Guid>();
        foreach (var node in saved)
        {
            if (node is not JsonObject item || item["name"]?.GetValue<string>() is not { } name ||
                item["enabled"]?.GetValue<bool>() is not { } enabled ||
                item["rules"] is not JsonArray { Count: <= MaximumRules } definitions ||
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

    private sealed record HighlightChoice(Guid? Id, string Name);
}
