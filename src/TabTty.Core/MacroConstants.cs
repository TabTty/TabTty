using System.Text;
using System.Text.Json;

namespace TabTty.Core;

internal sealed record MacroConstantsDeclaration(string Alias, string Path, MacroPosition Position);
internal sealed record MacroConstantsBinding(string Alias, MacroConstantsFile File);

internal sealed record MacroConstantsFile(string FullPath, string Digest, string Source, IReadOnlyDictionary<string, object> Values)
{
    internal const int MaxDeclarations = 16;
    internal const int MaxMembers = 4096;

    internal static MacroConstantsFile Parse(string fullPath, string digest, string source, int remainingMembers,
        Action<int>? admitMember = null)
    {
        byte[] json = Encoding.UTF8.GetBytes(source);
        var reader = new Utf8JsonReader(json);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw Error("ConstantsJson", reader.TokenStartIndex);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (values.Count >= remainingMembers) throw Error("ConstantsLimit", reader.TokenStartIndex);
                admitMember?.Invoke(1);
                string name = ReadString(ref reader);
                if (!IsIdentifier(name) || MacroChecker.IsReserved(name) || values.ContainsKey(name))
                    throw Error("ConstantsName", reader.TokenStartIndex);
                if (!reader.Read()) throw Error("ConstantsJson", reader.BytesConsumed);
                object value;
                switch (reader.TokenType)
                {
                    case JsonTokenType.String: value = ReadString(ref reader); break;
                    case JsonTokenType.True: value = true; break;
                    case JsonTokenType.False: value = false; break;
                    case JsonTokenType.Number:
                        if (reader.ValueSpan.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0)
                        {
                            if (!reader.TryGetInt64(out long integer)) throw Error("ConstantsType", reader.TokenStartIndex);
                            value = integer;
                        }
                        else
                        {
                            if (!reader.TryGetDouble(out double number) || !double.IsFinite(number))
                                throw Error("ConstantsType", reader.TokenStartIndex);
                            value = number;
                        }
                        break;
                    default: throw Error("ConstantsType", reader.TokenStartIndex);
                }
                values.Add(name, value);
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
                throw Error("ConstantsJson", reader.BytesConsumed);
        }
        catch (JsonException error)
        {
            // The JSON reader counts UTF-8 bytes and separates lines at LF.
            long offset = 0;
            for (long i = 0; i < error.LineNumber.GetValueOrDefault(); i++)
            {
                int next = Array.IndexOf(json, (byte)'\n', (int)offset);
                if (next < 0) break;
                offset = next + 1;
            }
            throw Error("ConstantsJson", offset + error.BytePositionInLine.GetValueOrDefault());
        }
        return new(fullPath, digest, source, values.AsReadOnly());

        string ReadString(ref Utf8JsonReader input)
        {
            try { return input.GetString()!; }
            catch (InvalidOperationException) // Includes an unpaired surrogate in a JSON escape.
            { throw Error("ConstantsJson", input.TokenStartIndex); }
        }

        MacroSourceException Error(string code, long byteOffset)
        {
            var prefix = json.AsSpan(0, (int)Math.Min(byteOffset, json.Length));
            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (prefix[i] is not ((byte)'\r' or (byte)'\n')) continue;
                if (prefix[i] == '\r' && i + 1 < prefix.Length && prefix[i + 1] == '\n') i++;
                line++;
                lineStart = i + 1;
            }
            return new(code, new(line, 1 + Encoding.UTF8.GetCharCount(prefix[lineStart..])), fullPath);
        }
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (char c in name.AsSpan(1))
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }
}
