using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TabTty.Core;

internal sealed partial class MacroRun
{
    private async ValueTask<object> BuiltinAsync(string name, object[] args)
    {
        string S(int i) => (string)args[i];
        long N(int i) => (long)args[i];
        MacroList L(int i) => (MacroList)args[i];
        StringComparison comparison = args.Length > 2 && args[^1] is true ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        switch (name)
        {
            case "Connect":
                if (connectAttempted) throw new MacroError(MacroErrorCode.ConnectionAlreadyAttempted);
                connectAttempted = true;
                string path = ResolvePath(S(0));
                await HostOperationAsync("Connect", async () =>
                {
                    try
                    {
                        TerminalSession connected = await host.ConnectAsync(this, path, attemptStop.Token).ConfigureAwait(false);
                        lock (gate) { session = connected; ready = true; }
                        return true;
                    }
                    catch (MacroError) { throw; }
                    catch (SshInteractionCancelledException) when (!stop.IsCancellationRequested)
                    { throw new MacroError(MacroErrorCode.UserCancelled); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                        ArgumentException or InvalidOperationException or System.Text.Json.JsonException or CryptographicException or
                        System.Net.Sockets.SocketException or InvalidDataException or NotSupportedException or
                        DllNotFoundException or EntryPointNotFoundException or FormatException or KeyNotFoundException ||
                        error is OperationCanceledException && !stop.IsCancellationRequested)
                    { throw new MacroError(MacroErrorCode.ConnectionFailed); }
                }, finishing: true).ConfigureAwait(false);
                return VoidValue;
            case "SetBaseDirectory": baseDirectory = ResolvePath(S(0)); return VoidValue;
            case "ResetBaseDirectory": baseDirectory = Path.GetDirectoryName(Program.FullPath)!; return VoidValue;
            case "BaseDirectory": return baseDirectory;
            case "Write": case "WriteLine":
                TerminalSession active = RequireSession();
                try { MacroLimits.Unicode(S(0), allowNul: true); }
                catch (MacroError) { throw new MacroError(MacroErrorCode.Encoding); }
                await HostOperationAsync(name, async () =>
                {
                    while (!await active.SendMacroTextAsync(S(0), name == "WriteLine", this, stop.Token).ConfigureAwait(false))
                        await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                return VoidValue;
            case "Credential":
                if (!Guid.TryParseExact(S(0), "D", out var vault) || !Guid.TryParseExact(S(1), "D", out var entry))
                    throw new MacroError(MacroErrorCode.InvalidArgument);
                return new CredentialReference(vault, entry, (MacroCredentialKind)args[2] == MacroCredentialKind.Password ? "password" : "privateKey");
            case "WritePassword": case "WritePasswordLine":
                await PasswordAsync(args.Length == 0 ? null : (CredentialReference)args[0], name == "WritePasswordLine").ConfigureAwait(false);
                return VoidValue;
            case "Literal": case "Regex": return new MacroPattern(S(0), name == "Regex", args.Length > 1 && (bool)args[1]);
            case "WaitLine": case "WaitPrompt":
                return await WaitAsync([(MacroPattern)args[0]], name == "WaitPrompt", args.Length == 2 ? N(1) : null).ConfigureAwait(false);
            case "WaitAnyLine": case "WaitAnyPrompt":
                if (L(0).Items.Count is < 1 or > 16) throw new MacroError(MacroErrorCode.InvalidArgument);
                return await WaitAsync(L(0).Items.Cast<MacroPattern>().ToArray(), name == "WaitAnyPrompt", args.Length == 2 ? N(1) : null).ConfigureAwait(false);
            case "MarkReceiveBoundary":
                RequireSession(); lock (gate) { history.MarkBoundary(); generation++; Pulse(); } return VoidValue;
            case "Sleep": await SleepAsync(N(0)).ConfigureAwait(false); return VoidValue;
            case "MinimizeWindow": case "RestoreWindow":
                await HostOperationAsync(name, async () => { await host.SetMinimizedAsync(name == "MinimizeWindow", stop.Token).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                return VoidValue;
            case "Dialog":
                var request = new MacroDialogRequest(S(0), S(1), (MacroDialogButtons)args[2],
                    args.Length == 5 && (bool)args[3], args.Length == 5 ? S(4) : "");
                ValidateDialog(request);
                return await HostOperationAsync(name, () => host.DialogAsync(request, stop.Token)).ConfigureAwait(false);
            case "Length": return (long)S(0).Length;
            case "Count": return (long)L(0).Items.Count;
            case "Substring":
                if (N(1) < 0 || N(1) > S(0).Length || N(2) < 0 || N(2) > S(0).Length - N(1))
                    throw new MacroError(MacroErrorCode.IndexOutOfRange);
                return S(0).Substring((int)N(1), (int)N(2));
            case "Contains": return S(0).Contains(S(1), comparison);
            case "StartsWith": return S(0).StartsWith(S(1), comparison);
            case "EndsWith": return S(0).EndsWith(S(1), comparison);
            case "IndexOf": return (long)S(0).IndexOf(S(1), comparison);
            case "Replace":
                if (S(1).Length == 0) throw new MacroError(MacroErrorCode.InvalidArgument);
                return ReplaceBounded(S(0), S(1), S(2));
            case "Split":
                if (S(1).Length == 0) throw new MacroError(MacroErrorCode.InvalidArgument);
                string[] split = S(0).Split(S(1), MacroLimits.ListItems + 1, StringSplitOptions.None);
                if (split.Length > MacroLimits.ListItems) throw new MacroError(MacroErrorCode.PayloadLimit);
                foreach (string text in split) Own(text);
                return new MacroList(MacroType.String, split);
            case "Join":
                long length = L(1).Items.Sum(item => (long)((string)item).Length) + Math.Max(0, L(1).Items.Count - 1) * (long)S(0).Length;
                if (length > MacroLimits.StringUnits) throw new MacroError(MacroErrorCode.PayloadLimit);
                return string.Join(S(0), L(1).Items.Cast<string>());
            case "Trim": return S(0).Trim(' ', '\t', '\r', '\n');
            case "ToUpperInvariant": return MacroLimits.String(S(0).ToUpperInvariant());
            case "ToLowerInvariant": return MacroLimits.String(S(0).ToLowerInvariant());
            case "Add": case "Insert":
                MacroList list = L(0);
                if (list.Items.Count == MacroLimits.ListItems) throw new MacroError(MacroErrorCode.PayloadLimit);
                int insertion = name == "Add" ? list.Items.Count : Index(args[1], list.Items.Count, insert: true);
                object item = ConvertValue(args[^1], list.Element);
                allocations.GetValue(list, _ => memory.Reserve(0)).Resize(32 + Math.Max(list.Items.Capacity, Math.Max(4, list.Items.Count * 2L)) * 16L);
                list.Items.Insert(insertion, item); list.Revision++; Own(list); return VoidValue;
            case "RemoveAt": L(0).Items.RemoveAt(Index(args[1], L(0).Items.Count)); L(0).Revision++; return VoidValue;
            case "Clear": L(0).Items.Clear(); L(0).Revision++; return VoidValue;
            case "Clone": return new MacroList(L(0).Element, L(0).Items);
            case "ParseLong": return ParseLong(S(0));
            case "ParseDouble": return ParseDouble(S(0));
            case "FormatNumber": return FormatNumber(args[0], S(1));
            case "Now": return clock.GetLocalNow();
            case "UtcNow": return clock.GetUtcNow();
            case "FormatTime": return FormatTime((DateTimeOffset)args[0], args.Length == 1 ? null : S(1));
            case "UnixMilliseconds": return ((DateTimeOffset)args[0]).ToUnixTimeMilliseconds();
            case "ElapsedMilliseconds": return ElapsedMilliseconds();
            default: throw new InvalidOperationException();
        }
    }

    private string ResolvePath(string path)
    {
        try
        {
            MacroLimits.Unicode(path);
            return OrdinaryWindowsPath.Resolve(path, baseDirectory);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException or PathTooLongException or MacroError)
        { throw new MacroError(MacroErrorCode.Path); }
    }

    internal bool TryCommitSend()
    {
        lock (gate)
        {
            stop.Token.ThrowIfCancellationRequested();
            if (holds != MacroHold.None) return false;
            inFlight = true; return true;
        }
    }

    private async Task PasswordAsync(CredentialReference? explicitReference, bool line)
    {
        TerminalSession active = RequireSession();
        CredentialReference reference = explicitReference ?? profilePassword ?? throw new MacroError(MacroErrorCode.MissingProfilePassword);
        if (reference.Kind != "password") throw new MacroError(MacroErrorCode.CredentialKindMismatch);
        if (transport is "telnet" or "serial")
        { lock (gate) plaintextNotice = true; Notify(); }
        await HostOperationAsync(line ? "WritePasswordLine" : "WritePassword", async () =>
        {
            while (true)
            {
                await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
                bool sent;
                using (VaultSecret? secret = await host.ReadPasswordAsync(reference, stop.Token).ConfigureAwait(false))
                {
                    if (secret is null) throw new MacroError(MacroErrorCode.VaultUnlockCancelled);
                    stop.Token.ThrowIfCancellationRequested();
                    sent = await active.SendMacroPasswordAsync(secret, line, this, stop.Token).ConfigureAwait(false);
                }
                if (sent) return true;
                // Dispose before suspending; the current entry is retrieved again after Resume.
            }
        }).ConfigureAwait(false);
    }

    internal static void ValidateDialog(MacroDialogRequest request)
    {
        if (request.Title.Length > 256 || request.Message.Length > 16384 || request.InitialText.Length > 4096)
            throw new MacroError(MacroErrorCode.PayloadLimit);
        MacroLimits.Unicode(request.Title); MacroLimits.Unicode(request.Message); MacroLimits.Unicode(request.InitialText);
        if (request.Title.Any(c => c < 32) || request.InitialText.Any(c => c < 32) || !request.InputEnabled && request.InitialText.Length != 0)
            throw new MacroError(MacroErrorCode.InvalidArgument);
    }

    private static string ReplaceBounded(string source, string oldValue, string newValue)
    {
        long length = source.Length;
        int offset = 0, index;
        while ((index = source.IndexOf(oldValue, offset, StringComparison.Ordinal)) >= 0)
        { length += (long)newValue.Length - oldValue.Length; offset = index + oldValue.Length; }
        if (length > MacroLimits.StringUnits) throw new MacroError(MacroErrorCode.PayloadLimit);
        return source.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static long ParseLong(string text)
    {
        if (!IsNumber(text, floating: false)) throw new MacroError(MacroErrorCode.NumberFormat);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            throw new MacroError(MacroErrorCode.NumericOverflow);
        return value;
    }
    private static double ParseDouble(string text)
    {
        if (!IsNumber(text, floating: true)) throw new MacroError(MacroErrorCode.NumberFormat);
        if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw new MacroError(MacroErrorCode.NumericOverflow);
        return value;
    }
    private static bool IsNumber(string text, bool floating)
    {
        if (text.Length == 0) return false;
        int i = text[0] is '+' or '-' ? 1 : 0;
        bool Digits() { int start = i; while (i < text.Length && char.IsAsciiDigit(text[i])) i++; return i != start; }
        if (!Digits()) return false;
        if (floating && i < text.Length && text[i] == '.') { i++; if (!Digits()) return false; }
        if (floating && i < text.Length && text[i] is 'e' or 'E')
        { i++; if (i < text.Length && text[i] is '+' or '-') i++; if (!Digits()) return false; }
        return i == text.Length;
    }
    private static string FormatNumber(object value, string format)
    {
        bool valid;
        if (value is long)
            valid = format is "G" or "D" or "X" || Precision(format, 'D', 1, 19) || Precision(format, 'X', 1, 16);
        else valid = format is "G" or "R" || Precision(format, 'F', 0, 15) || Precision(format, 'E', 0, 15);
        if (!valid) throw new MacroError(MacroErrorCode.Format);
        return ((IFormattable)value).ToString(format, CultureInfo.InvariantCulture);
    }
    private static bool Precision(string text, char prefix, int min, int max) => text.Length is 2 or 3 &&
        text[0] == prefix && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
        value >= min && value <= max && value.ToString(CultureInfo.InvariantCulture) == text[1..];

    private static string FormatTime(DateTimeOffset instant, string? format)
    {
        if (format is null) return MacroLimits.Format(instant);
        var pattern = new StringBuilder();
        for (int i = 0; i < format.Length;)
        {
            char c = format[i];
            if (c == '\'')
            {
                int end = format.IndexOf('\'', i + 1);
                if (end < 0) throw new MacroError(MacroErrorCode.Format);
                pattern.Append(format, i, end + 1 - i);
                i = end + 1;
                continue;
            }
            if (char.IsAsciiLetter(c))
            {
                int start = i; while (i < format.Length && format[i] == c) i++;
                int count = i - start;
                if (!(c == 'y' && count == 4 || c is 'M' or 'd' or 'H' or 'm' or 's' && count == 2 ||
                    c == 'f' && count is >= 1 and <= 7 || c == 'z' && count == 3)) throw new MacroError(MacroErrorCode.Format);
                if (count == 1) pattern.Append('%');
                pattern.Append(c, count);
            }
            else if (c is >= ' ' and <= '~' && !char.IsAsciiLetterOrDigit(c))
            {
                pattern.Append('\\').Append(c);
                i++;
            }
            else throw new MacroError(MacroErrorCode.Format);
        }
        try
        {
            if (format.Length == 0) return "";
            string result = instant.ToString(pattern.ToString(), CultureInfo.InvariantCulture);
            return MacroLimits.String(result);
        }
        catch (FormatException) { throw new MacroError(MacroErrorCode.Format); }
    }
}
