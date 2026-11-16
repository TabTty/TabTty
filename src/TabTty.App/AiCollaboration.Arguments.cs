using System.Text;
using System.Text.Json;
using TabTty.Core;

namespace TabTty.App;

internal sealed partial class AiCollaboration
{
    private readonly struct Arguments
    {
        private readonly JsonElement value;
        internal Arguments(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_request");
            this.value = value;
        }

        internal bool Has(string name) => value.TryGetProperty(name, out _);
        internal Arguments Object(string name) => value.TryGetProperty(name, out var property)
            ? new Arguments(property) : throw new ToolException("invalid_request");

        internal void Only(params string[] names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                    throw new ToolException("invalid_request");
        }

        internal string String(string name, int maximum, bool allowEmpty = false)
        {
            if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
                throw new ToolException("invalid_request");
            string text = property.GetString()!;
            if ((!allowEmpty && text.Length == 0) || text.Length > maximum * 2L ||
                text.EnumerateRunes().Count() > maximum) throw new ToolException("invalid_request");
            return text;
        }

        internal int Integer(string name, int minimum, int maximum, int? fallback = null)
        {
            if (!value.TryGetProperty(name, out var property) && fallback is { } initial) return initial;
            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int number) ||
                number < minimum || number > maximum) throw new ToolException("invalid_request");
            return number;
        }

        internal bool Boolean(string name, bool fallback)
        {
            if (!value.TryGetProperty(name, out var property)) return fallback;
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
            if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > 3)
                throw new ToolException("invalid_request");
            TerminalModifiers result = 0;
            foreach (var element in property.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) throw new ToolException("invalid_request");
                TerminalModifiers flag = element.GetString() switch
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
            switch (kind)
            {
                case "text":
                case "paste":
                    if (kind == "text") args.Only("kind", "text");
                    else args.Only("kind", "text", "character_delay_ms", "line_delay_ms");
                    string text = args.String("text", 256 * 1024);
                    if (TerminalPaste.Encoding.GetByteCount(text) > 256 * 1024)
                        throw new ToolException("resource_limit");
                    if (kind == "text" && text.Any(char.IsControl)) throw new ToolException("unsupported_action");
                    return new InputAction(kind, PasteText: text,
                        CharacterDelay: args.Integer("character_delay_ms", 0, 10_000, 0),
                        LineDelay: args.Integer("line_delay_ms", 0, 10_000, 0));
                case "key":
                    args.Only("kind", "key", "modifiers");
                    return new InputAction(kind, ParseKey(args.String("key", 16), args.Modifiers()));
                case "mouse":
                    string action = args.String("action", 7);
                    int button;
                    switch (action)
                    {
                        case "press":
                        case "release":
                            args.Only("kind", "action", "button", "row", "column", "modifiers");
                            button = args.String("button", 6) switch
                            {
                                "left" => 0, "middle" => 1, "right" => 2,
                                _ => throw new ToolException("invalid_request"),
                            };
                            break;
                        case "wheel":
                            args.Only("kind", "action", "wheel", "row", "column", "modifiers");
                            button = args.String("wheel", 4) switch
                            {
                                "up" => 4, "down" => 5, _ => throw new ToolException("invalid_request"),
                            };
                            break;
                        case "move":
                            args.Only("kind", "action", "row", "column", "modifiers");
                            button = 3;
                            break;
                        default: throw new ToolException("invalid_request");
                    }
                    return new InputAction(kind, Mouse: new AiMouseAction(action, button,
                        args.Integer("row", 1, 512), args.Integer("column", 1, 1024), args.Modifiers()));
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
