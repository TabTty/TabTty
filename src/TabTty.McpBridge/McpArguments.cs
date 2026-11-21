using System.Text.Json;

namespace TabTty.AiTransport;

internal static class McpArguments
{
    internal const int QueryUtf16Limit = 1024;
    private static readonly object Session = String(256);
    private static readonly object Cursor = String(256);
    private static readonly object Modifier = new { type = "array", maxItems = 3, uniqueItems = true,
        items = Choice("Shift", "Alt", "Ctrl") };
    internal static readonly IReadOnlyDictionary<string, JsonElement> Schemas =
        new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["tabtty_list_shared_sessions"] = Either(Object([("limit", Integer(1, 200, 50))], []), Object([("cursor", Cursor)], ["cursor"])),
        ["tabtty_read_state"] = WithCursor(Object([("session", Session), ("include_highlights", Boolean(true)),
                ("region", Object([("row", Integer(1, 512)), ("column", Integer(1, 1024)), ("rows", Integer(1, 512)),
                    ("columns", Integer(1, 1024))], ["row", "column", "rows", "columns"]))], ["session"])),
        ["tabtty_read_history"] = WithCursor(Object([("session", Session), ("range", Cursor), ("max_rows", Integer(1, 1000, 200)),
                ("max_text_bytes", Integer(1, 262144, 65536))], ["session"])),
        ["tabtty_search_history"] = WithCursor(Object([("session", Session), ("query", Query()), ("case_sensitive", Boolean(false)),
                ("limit", Integer(1, 200, 50)), ("context_lines", Integer(0, 10, 2))], ["session", "query"])),
        ["tabtty_wait"] = Either(Object([("session", Session), ("kind", Choice("change")), ("timeout_ms", Integer(0, 10000, 1000)),
                    ("observation", Cursor)], ["session", "kind", "observation"]),
                Object([("session", Session), ("kind", Choice("delay")), ("timeout_ms", Integer(0, 10000, 1000))],
                    ["session", "kind"])),
        ["tabtty_send_input"] = Object([("session", Session), ("control", Cursor), ("action_seq", Integer(1, int.MaxValue)), ("observation", Cursor),
                ("input", Input())], ["session", "control", "action_seq", "observation", "input"]),
        ["tabtty_return_control"] = Object([("session", Session), ("control", Cursor), ("reason", String(256, 0))], ["session", "control"]),
    }.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal);

    internal static bool IsQuery(string text) =>
        text.Length <= QueryUtf16Limit && !text.Contains('\r') && !text.Contains('\n');

    private static object Query() => new
    {
        type = "string", minLength = 1, maxLength = QueryUtf16Limit, pattern = @"^(?![\s\S]*[\r\n])[\s\S]+$",
        description = $"Search text, limited to {QueryUtf16Limit} UTF-16 code units. CR and LF are invalid."
    };

    internal static JsonElement Shape(JsonElement schema, int index) =>
        schema.TryGetProperty("oneOf", out var variants) ? variants[index] : schema;

    internal static bool TrySelectInputForm(JsonElement schema, string kind, string? action, out JsonElement form)
    {
        foreach (var candidate in schema.GetProperty("oneOf").EnumerateArray())
        {
            var fields = candidate.GetProperty("properties");
            if (Includes(fields.GetProperty("kind"), kind) &&
                (action is null || fields.TryGetProperty("action", out var actions) && Includes(actions, action)))
            {
                form = candidate;
                return true;
            }
        }
        form = default;
        return false;

        static bool Includes(JsonElement field, string value) =>
            field.GetProperty("enum").EnumerateArray().Any(choice => choice.GetString() == value);
    }

    private static object WithCursor(object initial) =>
        Either(initial, Object([("session", Session), ("cursor", Cursor)], ["session", "cursor"]));

    private static object Input() => Either(
        Object([("kind", Choice("text")), ("text", String(262144))], ["kind", "text"]),
        Object([("kind", Choice("paste")), ("text", String(262144)), ("character_delay_ms", Integer(0, 10000, 0)),
            ("line_delay_ms", Integer(0, 10000, 0))], ["kind", "text"]),
        Object([("kind", Choice("key")), ("key", new { type = "string", minLength = 1, maxLength = 16,
            description = "Enter, Backspace, Tab, Escape, ArrowUp/Down/Left/Right, Home, End, Insert, Delete, PageUp/Down, F1-F12, Numpad0-Numpad9, NumpadDecimal/Add/Subtract/Multiply/Divide/Enter, or one printable scalar." }),
            ("modifiers", Modifier)], ["kind", "key"]),
        Mouse(Choice("press", "release"), ("button", Choice("left", "middle", "right"))),
        Mouse(Choice("wheel"), ("wheel", Choice("up", "down"))),
        Mouse(Choice("move")));

    private static object Mouse(object action, (string Name, object Value)? extra = null)
    {
        var fields = new List<(string, object)> { ("kind", Choice("mouse")), ("action", action), ("row", Integer(1, 512)),
            ("column", Integer(1, 1024)), ("modifiers", Modifier) };
        var required = new List<string> { "kind", "action", "row", "column" };
        if (extra is { } field) { fields.Add(field); required.Add(field.Name); }
        return Object(fields, required);
    }

    private static object Object(IEnumerable<(string Name, object Value)> properties, IEnumerable<string> required) => new
    {
        type = "object", additionalProperties = false, properties = properties.ToDictionary(pair => pair.Name, pair => pair.Value),
        required = required.ToArray(),
    };
    private static object Either(params object[] forms) => new { type = "object", oneOf = forms };
    private static object Choice(params string[] choices) => new { type = "string", @enum = choices };
    private static object String(int max, int min = 1) => new { type = "string", minLength = min, maxLength = max };
    private static object Integer(int min, int max, int? value = null) => new { type = "integer", minimum = min,
        maximum = max, @default = value };
    private static object Boolean(bool value) => new { type = "boolean", @default = value };

}
