using System.Text;

namespace TabTty.Core;

public static class TerminalEncodings
{
    public static IReadOnlyList<string> Identifiers { get; } = Array.AsReadOnly(new[]
    {
        "utf-8", "cp932", "euc-jp", "jis", "cp949", "cp936", "cp950",
        "iso-8859-1", "iso-8859-2", "iso-8859-3", "iso-8859-4", "iso-8859-5", "iso-8859-6",
        "iso-8859-7", "iso-8859-8", "iso-8859-9", "iso-8859-10", "iso-8859-11", "iso-8859-13",
        "iso-8859-14", "iso-8859-15", "cp437", "cp737", "cp775", "cp850",
        "cp852", "cp855", "cp857", "cp860", "cp861", "cp862", "cp863", "cp864", "cp865",
        "cp866", "cp869", "cp874", "cp1250", "cp1251", "cp1252", "cp1253", "cp1254", "cp1255",
        "cp1256", "cp1257", "cp1258", "koi8-r",
    });
}

internal sealed class TerminalEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public TerminalEncoding(string identifier)
    {
        if (!TerminalEncodings.Identifiers.Contains(identifier, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported terminal encoding.", nameof(identifier));
        Id = identifier;
        NativePage = identifier switch
        {
            "utf-8" => 0,
            "euc-jp" => 51932,
            "jis" => 50220,
            "koi8-r" => 20866,
            _ when identifier.StartsWith("cp", StringComparison.Ordinal) => uint.Parse(identifier.AsSpan(2)),
            _ => 28590 + uint.Parse(identifier.AsSpan("iso-8859-".Length)),
        };
    }

    public string Id { get; }
    public bool IsUtf8 => NativePage == 0;
    internal uint NativePage { get; }

    internal string DecodeFileName(ReadOnlySpan<byte> bytes)
    {
        if (IsUtf8) return StrictUtf8.GetString(bytes);
        var text = new StringBuilder(bytes.Length);
        using var decoder = new LegacyReceiver(this, sevenBitKana: false);
        decoder.Decode(bytes, flush: false, 0, (rune, _) =>
        {
            if (rune == Rune.ReplacementChar) throw new DecoderFallbackException();
            text.Append(rune.ToString());
        }, () => false);
        if (decoder.HasIncompleteUnit) throw new DecoderFallbackException();
        return text.ToString();
    }

    public byte[] Encode(ReadOnlySpan<char> text, bool sevenBitKana = false)
    {
        if (IsUtf8)
            return NativeTerminalCore.PrepareUtf8Input(text, NativeTerminalCore.CheckUtf8Input(text));
        int length = NativeTerminalCore.CheckLegacyInput(NativePage, text, sevenBitKana);
        return NativeTerminalCore.PrepareLegacyInput(NativePage, text, sevenBitKana, length);
    }
}
