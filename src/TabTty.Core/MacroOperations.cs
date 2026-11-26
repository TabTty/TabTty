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
                LogCollisionPolicy? collision = null;
                if (args[MacroApi.LogCollision.Index] is string choice)
                {
                    if (!LogCollisionPolicies.TryParse(choice, out var policy)) throw new MacroError(MacroErrorCode.InvalidArgument);
                    collision = policy;
                }
                string path = ResolvePath(S(0));
                var recording = new RecordingOverrides(args[MacroApi.LogDirectory.Index] as string,
                    args[MacroApi.LogFilenameTemplate.Index] as string, collision, baseDirectory);
                MacroProgram? postConnection = null;
                try
                {
                    await HostOperationAsync("Connect", async () =>
                    {
                        try
                        {
                            var connected = await host.ConnectAsync(this, path, recording, attemptStop.Token).ConfigureAwait(false);
                            postConnection = connected.PostConnection;
                            lock (gate) { session = connected.Session; ready = true; }
                            return true;
                        }
                        catch (MacroError) { throw; }
                        catch (SshInteractionCancelledException) when (!stop.IsCancellationRequested)
                        { throw new MacroError(MacroErrorCode.UserCancelled); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                            ArgumentException or InvalidOperationException or System.Text.Json.JsonException or CryptographicException or
                            System.Net.Sockets.SocketException or InvalidDataException or NotSupportedException or
                            DllNotFoundException or EntryPointNotFoundException or FormatException or KeyNotFoundException or MacroSourceException ||
                            error is OperationCanceledException && !stop.IsCancellationRequested)
                        { throw new MacroError(MacroErrorCode.ConnectionFailed); }
                    }, finishing: true).ConfigureAwait(false);
                    if (postConnection is not null) await ExecutePostConnectionAsync(postConnection).ConfigureAwait(false);
                }
                finally { postConnection?.Dispose(); }
                return VoidValue;
            case "SetBaseDirectory": baseDirectory = ResolvePath(S(0)); return VoidValue;
            case "ResetBaseDirectory": baseDirectory = Path.GetDirectoryName(Program.FullPath)!; return VoidValue;
            case "BaseDirectory": return baseDirectory;
            case "GetParentDirectory": return GetParentDirectory(S(0));
            case "GetFileBaseName": return GetFileBaseName(S(0));
            case "GetFileExtension": return GetFileExtension(S(0));
            case "Write": case "WriteLine":
                TerminalSession active = RequireSession();
                bool markReceiveBoundary = name == "WriteLine" && args[MacroApi.MarkReceiveBoundary.Index] is true;
                try { MacroLimits.Unicode(S(0), allowNul: true); }
                catch (MacroError) { throw new MacroError(MacroErrorCode.Encoding); }
                await HostOperationAsync(name, async () =>
                {
                    while (!await active.SendMacroTextAsync(S(0), name == "WriteLine", markReceiveBoundary,
                        this, stop.Token).ConfigureAwait(false))
                        await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                return VoidValue;
            case "WritePassword": case "WritePasswordLine":
                await PasswordAsync(args.Length == 0 ? null : S(0), name == "WritePasswordLine").ConfigureAwait(false);
                return VoidValue;
            case "Literal": case "Regex": return new MacroPattern(S(0), name == "Regex", args.Length > 1 && (bool)args[1]);
            case "WaitLine": case "WaitPrompt":
                return await WaitAsync([new("", (MacroPattern)args[0])], name == "WaitPrompt", args.Length == 2 ? N(1) : null).ConfigureAwait(false);
            case "WaitAnyLine": case "WaitAnyPrompt":
                return await WaitAsync((MacroCandidate[])args[0], name == "WaitAnyPrompt", args.Length == 2 ? N(1) : null).ConfigureAwait(false);
            case "MarkReceiveBoundary":
                RequireSession(); CommitReceiveBoundary(); return VoidValue;
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
                list.Insert(insertion, item, allocations.GetValue(list, _ => memory.Reserve(0)).Resize);
                return VoidValue;
            case "RemoveAt":
                MacroList removal = L(0);
                removal.RemoveAt(Index(args[1], removal.Items.Count));
                return VoidValue;
            case "Clear": L(0).Clear(); return VoidValue;
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

    private static string GetParentDirectory(string path)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        int rootLength = Path.GetPathRoot(path.AsSpan()).Length;
        int end = path.Length - fileName.Length;
        while (end > rootLength &&
            (path[end - 1] == Path.DirectorySeparatorChar || path[end - 1] == Path.AltDirectorySeparatorChar))
            end--;

        return end == rootLength && fileName.IsEmpty ? string.Empty : path[..end];
    }

    private static string GetFileBaseName(string path)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        return fileName[..ExtensionStart(fileName)].ToString();
    }

    private static string GetFileExtension(string path)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        return fileName[ExtensionStart(fileName)..].ToString();
    }

    private static int ExtensionStart(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 && dot < fileName.Length - 1 ? dot : fileName.Length;
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

    private PasswordPreparation? passwordPreparation;

    private sealed class PasswordPreparation(CancellationToken stop) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        private Task? invalidation;
        internal CancellationToken Token => cancellation.Token;
        internal void Invalidate() => invalidation ??= cancellation.CancelAsync();

        public void Dispose()
        {
            if (invalidation is { IsCompleted: false } pending)
                _ = pending.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
            else cancellation.Dispose();
        }
    }

    internal MacroReceiveCutoff? CommitSend(CancellationToken preparation, bool markReceiveBoundary, bool first)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) throw new MacroSendDeferredException();
            if (!first) return null;
            if (holds != MacroHold.None || preparation.IsCancellationRequested)
                throw new MacroSendDeferredException();
            inFlight = true;
            return markReceiveBoundary ? history.Cutoff() : null;
        }
    }

    internal void CommitReceiveBoundary(MacroReceiveCutoff? cutoff = null)
    {
        lock (gate)
        {
            history.MarkBoundary(cutoff ?? history.Cutoff());
            generation++;
            Pulse();
        }
    }

    private async Task PasswordAsync(string? explicitName, bool line)
    {
        TerminalSession active = RequireSession();
        if (explicitName is not null && !VaultPayload.IsValidName(explicitName))
            throw new MacroError(MacroErrorCode.InvalidArgument);
        CredentialReference reference = explicitName is not null ? new(explicitName, "password") :
            active.ConfiguredSshPassword ?? throw new MacroError(MacroErrorCode.MissingProfilePassword);
        if (active.IsTelnet || active.IsSerial)
        {
            bool showNotice;
            lock (gate) { showNotice = !plaintextNotice; plaintextNotice = true; }
            if (showNotice) Notify();
        }
        await HostOperationAsync(line ? "WritePasswordLine" : "WritePassword", async () =>
        {
            while (true)
            {
                await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
                using var preparation = new PasswordPreparation(stop.Token);
                lock (gate)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (holds != MacroHold.None) continue;
                    passwordPreparation = preparation;
                }
                try
                {
                    using VaultSecret? secret = await host.ReadPasswordAsync(reference, stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    if (preparation.Token.IsCancellationRequested) continue;
                    if (secret is null) throw new MacroError(MacroErrorCode.VaultUnlockCancelled);
                    if (await active.SendMacroPasswordAsync(secret, line, preparation.Token, this, stop.Token).ConfigureAwait(false))
                        return true;
                }
                catch (MacroError) when (preparation.Token.IsCancellationRequested && !stop.IsCancellationRequested) { }
                finally
                {
                    lock (gate) passwordPreparation = null;
                }
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
