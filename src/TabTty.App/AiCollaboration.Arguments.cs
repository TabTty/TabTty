using System.Text;
using System.Text.Json;
using TabTty.Core;
using TabTty.AiTransport;

namespace TabTty.App;

internal sealed partial class AiCollaboration
{
    private readonly struct Arguments
    {
        private readonly JsonElement value;
        private readonly JsonElement schema;
        internal Arguments(JsonElement value, JsonElement schema)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_request");
            this.value = value;
            this.schema = schema;
        }

        internal bool Has(string name) => value.TryGetProperty(name, out _);
        internal Arguments Object(string name) => value.TryGetProperty(name, out var property)
            ? new Arguments(property, Field(name)) : throw new ToolException("invalid_request");

        internal Arguments SelectInputForm(string kind, string? action)
        {
            if (!McpArguments.TrySelectInputForm(schema, kind, action, out var form))
                throw new ToolException(action is null ? "unsupported_action" : "invalid_request");
            return new Arguments(value, form);
        }

        private JsonElement Field(string name)
        {
            if (!schema.TryGetProperty("oneOf", out var variants))
                return schema.GetProperty("properties").GetProperty(name);
            foreach (var form in variants.EnumerateArray())
                if (form.GetProperty("properties").TryGetProperty(name, out var field)) return field;
            throw new ToolException("invalid_request");
        }

        internal void Only(int variant = 0)
        {
            var shape = McpArguments.Shape(schema, variant);
            var fields = shape.GetProperty("properties");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!fields.TryGetProperty(property.Name, out _) || !seen.Add(property.Name))
                    throw new ToolException("invalid_request");
        }

        internal string String(string name, int enumLimit = 0)
        {
            if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
                throw new ToolException("invalid_request");
            var field = Field(name);
            int maximum = field.TryGetProperty("maxLength", out var bound) ? bound.GetInt32() : enumLimit;
            bool allowEmpty = field.TryGetProperty("minLength", out var min) && min.GetInt32() == 0;
            string text = property.GetString()!;
            if ((!allowEmpty && text.Length == 0) || text.Length > maximum * 2L ||
                text.EnumerateRunes().Count() > maximum) throw new ToolException("invalid_request");
            return text;
        }

        internal int Integer(string name, int? fallback = null)
        {
            var field = Field(name);
            if (!value.TryGetProperty(name, out var property))
            {
                if (fallback is { } initial) return initial;
                if (field.TryGetProperty("default", out var configured) && configured.ValueKind == JsonValueKind.Number)
                    return configured.GetInt32();
            }
            int minimum = field.GetProperty("minimum").GetInt32();
            int maximum = field.GetProperty("maximum").GetInt32();
            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int number) ||
                number < minimum || number > maximum) throw new ToolException("invalid_request");
            return number;
        }

        internal bool Boolean(string name)
        {
            if (!value.TryGetProperty(name, out var property)) return Field(name).GetProperty("default").GetBoolean();
            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ToolException("invalid_request"),
            };
        }

        internal TerminalModifiers Modifiers()
        {
            if (!value.TryGetProperty("modifiers", out var property)) return TerminalModifiers.None;
            var field = Field("modifiers");
            if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > field.GetProperty("maxItems").GetInt32())
                throw new ToolException("invalid_request");
            var choices = field.GetProperty("items").GetProperty("enum");
            TerminalModifiers result = 0;
            foreach (var element in property.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) throw new ToolException("invalid_request");
                string? name = element.GetString();
                if (!choices.EnumerateArray().Any(choice => choice.GetString() == name))
                    throw new ToolException("invalid_request");
                TerminalModifiers flag = name switch
                {
                    "Shift" => TerminalModifiers.Shift,
                    "Alt" => TerminalModifiers.Alt,
                    "Ctrl" => TerminalModifiers.Control,
                    _ => throw new ToolException("invalid_request"),
                };
                if ((result & flag) != 0) throw new ToolException("invalid_request");
                result |= flag;
            }
            return result;
        }
    }

    private sealed record InputAction(string Kind, TerminalInput Input = default, AiMouseAction? Mouse = null,
        string? PasteText = null, int CharacterDelay = 0, int LineDelay = 0)
    {
        internal static InputAction Parse(Arguments args)
        {
            string kind = args.String("kind", 5);
            string? mouseAction = kind == "mouse" ? args.String("action", 7) : null;
            args = args.SelectInputForm(kind, mouseAction);
            args.Only();
            switch (kind)
            {
                case "text":
                case "paste":
                    string text = args.String("text");
                    if (TerminalPaste.Encoding.GetByteCount(text) > 256 * 1024)
                        throw new ToolException("resource_limit");
                    if (kind == "text" && text.Any(char.IsControl)) throw new ToolException("unsupported_action");
                    return new InputAction(kind, PasteText: text,
                        CharacterDelay: kind == "paste" ? args.Integer("character_delay_ms") : 0,
                        LineDelay: kind == "paste" ? args.Integer("line_delay_ms") : 0);
                case "key":
                    return new InputAction(kind, ParseKey(args.String("key"), args.Modifiers()));
                case "mouse":
                    string action = mouseAction!;
                    int button;
                    switch (action)
                    {
                        case "press":
                        case "release":
                            button = args.String("button", 6) switch
                            {
                                "left" => 0, "middle" => 1, "right" => 2,
                                _ => throw new ToolException("invalid_request"),
                            };
                            break;
                        case "wheel":
                            button = args.String("wheel", 4) switch
                            {
                                "up" => 4, "down" => 5, _ => throw new ToolException("invalid_request"),
                            };
                            break;
                        case "move":
                            button = 3;
                            break;
                        default: throw new ToolException("invalid_request");
                    }
                    return new InputAction(kind, Mouse: new AiMouseAction(action, button,
                        args.Integer("row"), args.Integer("column"), args.Modifiers()));
                default:
                    throw new ToolException("unsupported_action");
            }
        }

        private static TerminalInput ParseKey(string name, TerminalModifiers modifiers)
        {
            if (modifiers == TerminalModifiers.None)
            {
                if (name == "Enter") return new(TerminalInputKind.Enter);
                if (name == "Backspace") return new(TerminalInputKind.Backspace);
                if (name == "Tab") return new(TerminalInputKind.Tab);
            }
            if (name == "Tab" && modifiers == TerminalModifiers.Shift)
                return new(TerminalInputKind.Key, Key: TerminalKey.BackTab);
            if (Rune.TryGetRuneAt(name, 0, out Rune rune) && rune.Utf16SequenceLength == name.Length &&
                !Rune.IsControl(rune))
            {
                if (modifiers == TerminalModifiers.Control && rune.IsAscii && char.IsAsciiLetter((char)rune.Value))
                    return new(TerminalInputKind.Control, char.ToUpperInvariant((char)rune.Value).ToString());
                if (modifiers is TerminalModifiers.None or TerminalModifiers.Alt or TerminalModifiers.Shift or
                    (TerminalModifiers.Alt | TerminalModifiers.Shift))
                    return new(TerminalInputKind.Text, name, Alt: modifiers.HasFlag(TerminalModifiers.Alt));
                throw new ToolException("unsupported_action");
            }
            string mapped = name switch
            {
                "ArrowUp" => "Up", "ArrowDown" => "Down", "ArrowLeft" => "Left", "ArrowRight" => "Right",
                _ when name.StartsWith("Numpad", StringComparison.Ordinal) => "Keypad" + name[6..],
                _ => name,
            };
            if (!Enum.TryParse(mapped, out TerminalKey key) || key.ToString() != mapped ||
                key is TerminalKey.BackTab || name == mapped && key is TerminalKey.Up or TerminalKey.Down or TerminalKey.Left or TerminalKey.Right ||
                key >= TerminalKey.Keypad0 && (!name.StartsWith("Numpad", StringComparison.Ordinal) || modifiers != 0) ||
                key == TerminalKey.Escape && modifiers != 0)
                throw new ToolException("unsupported_action");
            return new(TerminalInputKind.Key, Key: key, Modifiers: modifiers);
        }
    }
}
