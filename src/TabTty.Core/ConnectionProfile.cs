using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabTty.Core;

public sealed record JapaneseSettings(string KanjiIn = "B", string KanjiOut = "B",
    string ReceiveKana = "eightBit", string TransmitKana = "eightBit");

public sealed record SerialSettings(string PortName, uint BaudRate = 9600, int DataBits = 8,
    string Parity = "none", int StopBits = 1, string FlowControl = "none", bool Dtr = true, bool Rts = true);

public sealed record CredentialReference(Guid VaultId, Guid EntryId, string Kind);

public sealed class ConnectionProfile
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    private readonly JsonElement document;
    private byte[]? sourceBytes;
    private string? sourcePath;
    private JsonElement Connection => document.GetProperty("connection");
    private JsonElement Terminal => document.GetProperty("terminal");
    private JsonElement Logging => document.GetProperty("logging");

    private ConnectionProfile(JsonElement document) => this.document = document;

    public string DisplayName => document.GetProperty("displayName").GetString()!;
    public Guid Id => document.GetProperty("profileId").GetGuid();
    public string Type => Connection.GetProperty("type").GetString()!;
    public string Host => Connection.GetProperty("host").GetString()!;
    public int Port => Connection.GetProperty("port").GetInt32();
    public int WaitSeconds => Connection.GetProperty("waitSeconds").GetInt32();
    public bool InitiateBinary => Connection.GetProperty("initiateBinary").GetBoolean();
    public string SshUsername => Connection.GetProperty("username").GetString()!;
    public CredentialReference? SshPassword => ReadCredential(Connection.GetProperty("authentication"), "password");
    public CredentialReference? SshPrivateKey => ReadCredential(Connection.GetProperty("authentication"), "privateKey");
    public IReadOnlyList<string> SshMethods => Connection.GetProperty("authentication").GetProperty("methods")
        .EnumerateArray().Select(static value => value.GetString()!).ToArray();
    public bool SshLivenessEnabled => Connection.GetProperty("liveness").GetProperty("enabled").GetBoolean();
    public int SshLivenessInterval => Connection.GetProperty("liveness").GetProperty("intervalSeconds").GetInt32();
    public SerialSettings Serial => new(Connection.GetProperty("portName").GetString()!,
        Connection.GetProperty("baudRate").GetUInt32(), Connection.GetProperty("dataBits").GetInt32(),
        Connection.GetProperty("parity").GetString()!, Connection.GetProperty("stopBits").GetInt32(),
        Connection.GetProperty("flowControl").GetString()!, Connection.GetProperty("dtr").GetBoolean(),
        Connection.GetProperty("rts").GetBoolean());
    public string AdvertisedType => Terminal.GetProperty("advertisedType").GetString()!;
    public string ReceiveEncoding => Terminal.GetProperty("receiveEncoding").GetString()!;
    public string TransmitEncoding => Terminal.GetProperty("sameEncoding").GetBoolean()
        ? ReceiveEncoding : Terminal.GetProperty("transmitEncoding").GetString()!;
    private JsonElement Japanese => Terminal.GetProperty("jis");
    internal bool ReceiveSevenBitKana => Japanese.GetProperty("receiveKana").GetString() == "soSi";
    internal bool TransmitSevenBitKana => Japanese.GetProperty("transmitKana").GetString() == "soSi";
    public string Enter => Terminal.GetProperty("enter").GetString()!;
    public string Backspace => Terminal.GetProperty("backspace").GetString()!;
    public string LocalEcho => Terminal.GetProperty("localEcho").GetString()!;
    public int AmbiguousWidth => Terminal.GetProperty("ambiguousWidth").GetInt32();
    public int? RetainedRows => Terminal.GetProperty("retainedRows").GetProperty("value").Deserialize<int?>();
    public bool RecordRaw => Logging.GetProperty("raw").GetBoolean();
    public bool RecordText => Logging.GetProperty("text").GetBoolean();
    public bool RecordTerminal => Logging.GetProperty("terminal").GetBoolean();
    public bool RecordsOutput => RecordRaw || RecordText || RecordTerminal;
    public string LogDirectory => Logging.GetProperty("directory").GetString()!;
    public Guid? HighlightSetId => Terminal.GetProperty("highlightSetId").Deserialize<Guid?>();
    public string? FontFamily => Terminal.GetProperty("font").GetProperty("family").Deserialize<string?>();
    public double? FontSizePoints => Terminal.GetProperty("font").GetProperty("sizePoints").Deserialize<double?>();
    public string ToEditorJson() => JsonSerializer.Serialize(document, SaveOptions);

    public void ValidateEditorSettings()
    {
        string path = LogDirectory;
        if (path.IndexOf('\0') >= 0 || path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) ||
            path.AsSpan(Path.IsPathFullyQualified(path) && path.Length > 1 && path[1] == ':' ? 2 : 0).Contains(':'))
            throw new InvalidDataException("The log directory must be an ordinary relative or absolute Windows path.");
        _ = Path.GetFullPath(path, Path.GetDirectoryName(sourcePath ?? Path.Combine(Environment.CurrentDirectory, "profile.ttty"))!);
    }

    public ConnectionProfile Edit(string json)
    {
        if (json.Length > MaximumBytes || StrictUtf8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Connection profile exceeds 1 MiB.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        ConnectionProfile revision = Validate(parsed.RootElement.Clone());
        if (revision.Id != Id) throw new InvalidDataException("An edit must preserve the profile ID.");
        revision.sourcePath = sourcePath;
        revision.sourceBytes = sourceBytes;
        return revision;
    }

    public ConnectionProfile Duplicate()
    {
        JsonObject copy = JsonNode.Parse(document.GetRawText())!.AsObject();
        copy["profileId"] = Guid.NewGuid();
        return Validate(JsonSerializer.SerializeToElement(copy));
    }

    public static ConnectionProfile CreateTelnet(string name, string host, int port = 23, bool recordRaw = false,
        string receiveEncoding = "utf-8", string? transmitEncoding = null, JapaneseSettings? japanese = null,
        int ambiguousWidth = 1, bool recordText = false, bool recordTerminal = false, Guid? highlightSetId = null)
        => Create(name, new { type = "telnet", waitSeconds = 30, host, port, initiateBinary = false },
            "crlf", "del", "followRemote", recordRaw, receiveEncoding, transmitEncoding, japanese, ambiguousWidth,
            recordText, recordTerminal, highlightSetId);

    public static ConnectionProfile CreateSerial(string name, SerialSettings serial, bool localEcho = false,
        bool recordRaw = false, string receiveEncoding = "utf-8", string? transmitEncoding = null,
        JapaneseSettings? japanese = null, int ambiguousWidth = 1, bool recordText = false,
        bool recordTerminal = false, Guid? highlightSetId = null) => Create(name, new
        {
            type = "serial", waitSeconds = 30, portName = serial.PortName,
            baudRate = serial.BaudRate, dataBits = serial.DataBits, parity = serial.Parity,
            stopBits = serial.StopBits, flowControl = serial.FlowControl, dtr = serial.Dtr, rts = serial.Rts,
        }, "cr", "bs", localEcho ? "on" : "off", recordRaw, receiveEncoding, transmitEncoding, japanese, ambiguousWidth,
            recordText, recordTerminal, highlightSetId);

    public static ConnectionProfile CreateSsh(string name, string host, string username, int port = 22,
        CredentialReference? password = null, bool recordRaw = false, string receiveEncoding = "utf-8",
        string? transmitEncoding = null, JapaneseSettings? japanese = null, int ambiguousWidth = 1,
        bool recordText = false, bool recordTerminal = false, Guid? highlightSetId = null,
        IReadOnlyList<string>? methods = null, CredentialReference? privateKey = null) => Create(name, new
        {
            type = "ssh", waitSeconds = 30, host, port, username,
            authentication = new
            {
                methods = methods ?? ["password"],
                privateKey = privateKey is null ? null : new
                {
                    vaultId = privateKey.VaultId, entryId = privateKey.EntryId, kind = privateKey.Kind,
                },
                password = password is null ? null : new
                {
                    vaultId = password.VaultId, entryId = password.EntryId, kind = password.Kind,
                },
            },
            liveness = new { enabled = false, intervalSeconds = 30, unansweredWindows = 3 },
        }, "cr", "del", "off", recordRaw, receiveEncoding, transmitEncoding, japanese, ambiguousWidth,
            recordText, recordTerminal, highlightSetId);

    private static ConnectionProfile Create(string name, object connection, string enter, string backspace,
        string localEcho, bool recordRaw, string receiveEncoding, string? transmitEncoding,
        JapaneseSettings? japanese, int ambiguousWidth, bool recordText, bool recordTerminal, Guid? highlightSetId)
        => Validate(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            requiredFeatures = Array.Empty<string>(),
            profileId = Guid.NewGuid().ToString("D"),
            displayName = name,
            connection,
            terminal = new
            {
                advertisedType = "xterm", receiveEncoding, sameEncoding = transmitEncoding is null,
                transmitEncoding = transmitEncoding ?? receiveEncoding,
                jis = new { kanjiIn = japanese?.KanjiIn ?? "B", kanjiOut = japanese?.KanjiOut ?? "B",
                    receiveKana = japanese?.ReceiveKana ?? "eightBit", transmitKana = japanese?.TransmitKana ?? "eightBit" },
                ambiguousWidth, enter, backspace, localEcho,
                font = new { mode = "inherit", family = (string?)null, sizePoints = (int?)null },
                retainedRows = new { mode = "inherit", value = (int?)null },
                highlightSetId,
            },
            logging = new { raw = recordRaw, text = recordText, terminal = recordTerminal, directory = "Logs" },
        }));

    public static async Task<ConnectionProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await ApplicationResources.FilePreparations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadPreparedAsync(path, cancellationToken).ConfigureAwait(false); }
        finally { ApplicationResources.FilePreparations.Release(); }
    }

    private static async Task<ConnectionProfile> LoadPreparedAsync(string path, CancellationToken cancellationToken)
    {
        string source = OpeningPath(path);
        byte[] bytes = await ReadBytesAsync(source, cancellationToken).ConfigureAwait(false);
        int start = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
        try
        {
            StrictUtf8.GetCharCount(bytes, start, bytes.Length - start);
            using JsonDocument parsed = JsonDocument.Parse(bytes.AsMemory(start),
                new JsonDocumentOptions { MaxDepth = 16 });
            var profile = Validate(parsed.RootElement.Clone());
            profile.sourcePath = source;
            profile.sourceBytes = bytes;
            return profile;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("The connection profile contains invalid UTF-8 JSON.", exception);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] bytes = new byte[MaximumBytes + 1];
        int length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken)
            .ConfigureAwait(false);
        if (length > MaximumBytes)
        {
            throw new InvalidDataException("Connection profile exceeds 1 MiB.");
        }

        return bytes[..length];
    }

    public Task SaveNewAsync(string path, CancellationToken cancellationToken = default) =>
        SaveAsync(OpeningPath(path), null, cancellationToken);

    private static string OpeningPath(string path)
    {
        if (path.Length > 32760 || path.Contains('\0') || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) ||
            path.AsSpan(path.Length > 1 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? 2 : 0).Contains(':'))
            throw new InvalidDataException("Use an ordinary relative, DOS or UNC profile path.");
        string full = Path.GetFullPath(path);
        if (full.Length > 32760) throw new InvalidDataException("The profile path exceeds its size limit.");
        return full;
    }

    public Task SaveEditAsync(CancellationToken cancellationToken = default)
    {
        if (sourcePath is null || sourceBytes is null)
            throw new InvalidOperationException("Load a saved profile before editing it.");
        return SaveAsync(sourcePath, sourceBytes, cancellationToken);
    }

    private async Task SaveAsync(string destination, byte[]? original, CancellationToken cancellationToken)
    {
        ValidateEditorSettings();
        string directory = Path.GetDirectoryName(destination)!;
        string candidate = Path.Combine(directory, $".tabtty-{Guid.NewGuid():N}.tmp");
        bool ownsCandidate = false;
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(document, SaveOptions);
        if (serialized.Length > MaximumBytes) throw new InvalidDataException("Connection profile exceeds 1 MiB.");
        cancellationToken.ThrowIfCancellationRequested();
        if (original is null) Directory.CreateDirectory(directory);

        try
        {
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 8192, FileOptions.Asynchronous))
            {
                ownsCandidate = true;
                await stream.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (original is null) File.Move(candidate, destination, overwrite: false);
            else
            {
                byte[] effective = await ReadBytesAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!effective.AsSpan().SequenceEqual(original))
                    throw new IOException("The saved profile changed outside the editor. Reopen it before saving.");
                cancellationToken.ThrowIfCancellationRequested();
                IOException? replacementError = null;
                try { File.Replace(candidate, destination, destination + ".bak"); }
                catch (IOException exception) { replacementError = exception; }
                catch (UnauthorizedAccessException exception)
                { replacementError = new IOException("Profile replacement failed.", exception); }
                try { effective = await ReadBytesAsync(destination, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    ownsCandidate = false;
                    throw new IOException("The profile save could not be verified.", exception);
                }
                if (!effective.AsSpan().SequenceEqual(serialized))
                {
                    if (replacementError is not null && effective.AsSpan().SequenceEqual(original)) throw replacementError;
                    ownsCandidate = false;
                    throw new IOException("Profile replacement conflicted with another file change.");
                }
            }
            ownsCandidate = false;
            sourcePath = destination;
            sourceBytes = serialized;
        }
        finally
        {
            if (ownsCandidate)
            {
                File.Delete(candidate);
            }
        }
    }

    private static ConnectionProfile Validate(JsonElement root)
    {
        RejectDuplicateProperties(root);
        if (Integer(root, "schemaVersion", 1, 1) != 1 ||
            Field(root, "requiredFeatures", JsonValueKind.Array).GetArrayLength() != 0)
        {
            throw new InvalidDataException("Unsupported profile version or required feature.");
        }

        Uuid(Text(root, "profileId", 36));
        Text(root, "displayName", 256);
        var connection = Field(root, "connection", JsonValueKind.Object);
        Integer(connection, "waitSeconds", 1, 300);
        string type = Choice(connection, "type", "telnet", "ssh", "serial");
        if (type == "telnet")
        {
            string host = Text(connection, "host", 253);
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Any(char.IsWhiteSpace))
            {
                throw new InvalidDataException("Invalid Telnet host.");
            }

            Integer(connection, "port", 1, 65535);
            Boolean(connection, "initiateBinary");
        }
        else if (type == "ssh")
        {
            _ = SshDestination.Parse(Text(connection, "host", 253), Integer(connection, "port", 1, 65535));
            string username = Text(connection, "username", 1024);
            if (StrictUtf8.GetByteCount(username) > 1024 || username.Any(char.IsControl))
                throw new InvalidDataException("Invalid SSH username.");
            var authentication = Field(connection, "authentication", JsonValueKind.Object);
            var methods = Field(authentication, "methods", JsonValueKind.Array);
            if (methods.GetArrayLength() is < 1 or > 3 || methods.EnumerateArray().Any(static method =>
                method.ValueKind != JsonValueKind.String || method.GetString() is not ("publicKey" or "password" or "keyboardInteractive")) ||
                methods.EnumerateArray().Select(static method => method.GetString()).Distinct().Count() != methods.GetArrayLength())
                throw new InvalidDataException("Invalid SSH authentication methods.");
            _ = ReadCredential(authentication, "privateKey");
            _ = ReadCredential(authentication, "password");
            var liveness = Field(connection, "liveness", JsonValueKind.Object);
            Boolean(liveness, "enabled");
            Integer(liveness, "intervalSeconds", 1, 3600);
            Integer(liveness, "unansweredWindows", 3, 3);
        }
        else if (type == "serial")
        {
            if (!IsComPortName(Text(connection, "portName", 13)))
                throw new InvalidDataException("Invalid COM port name.");
            if (!Field(connection, "baudRate", JsonValueKind.Number).TryGetUInt32(out uint baudRate) || baudRate == 0)
                throw new InvalidDataException("Invalid serial baud rate.");
            Integer(connection, "dataBits", 7, 8);
            Integer(connection, "stopBits", 1, 2);
            Choice(connection, "parity", "none", "odd", "even", "mark", "space");
            Choice(connection, "flowControl", "none", "xonXoff", "rtsCts", "dsrDtr");
            Boolean(connection, "dtr");
            Boolean(connection, "rts");
        }

        var terminal = Field(root, "terminal", JsonValueKind.Object);
        if (Text(terminal, "advertisedType", 64).Any(character => character is < '!' or > '~'))
        {
            throw new InvalidDataException("Invalid advertised terminal type.");
        }

        if (!TerminalEncodings.Identifiers.Contains(Text(terminal, "receiveEncoding", 32), StringComparer.Ordinal) ||
            !TerminalEncodings.Identifiers.Contains(Text(terminal, "transmitEncoding", 32), StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported terminal encoding.");
        Boolean(terminal, "sameEncoding");
        Choice(terminal, "enter", "cr", "lf", "crlf");
        Choice(terminal, "backspace", "bs", "del");
        Choice(terminal, "localEcho", type switch
        {
            "telnet" => ["off", "followRemote"],
            "serial" => ["off", "on"],
            _ => ["off"],
        });
        Integer(terminal, "ambiguousWidth", 1, 2);
        var jis = Field(terminal, "jis", JsonValueKind.Object);
        Choice(jis, "kanjiIn", "@", "B");
        Choice(jis, "kanjiOut", "B", "J");
        Choice(jis, "receiveKana", "eightBit", "soSi");
        Choice(jis, "transmitKana", "eightBit", "soSi");
        var font = Field(terminal, "font", JsonValueKind.Object);
        if (Choice(font, "mode", "inherit", "override") == "inherit")
        {
            Field(font, "family", JsonValueKind.Null);
            Field(font, "sizePoints", JsonValueKind.Null);
        }
        else
        {
            Text(font, "family", 256);
            var size = Field(font, "sizePoints", JsonValueKind.Number);
            if (!size.TryGetDouble(out double points) || points is < 6 or > 72)
                throw new InvalidDataException("Invalid terminal font size.");
        }

        var retention = Field(terminal, "retainedRows", JsonValueKind.Object);
        if (Choice(retention, "mode", "inherit", "override") == "inherit")
            Field(retention, "value", JsonValueKind.Null);
        else
            Integer(retention, "value", 100, 100_000);
        if (Value(terminal, "highlightSetId").ValueKind != JsonValueKind.Null)
            Uuid(Text(terminal, "highlightSetId", 36));

        var logging = Field(root, "logging", JsonValueKind.Object);
        Boolean(logging, "raw");
        Boolean(logging, "text");
        Boolean(logging, "terminal");
        Text(logging, "directory", 32760);
        return new ConnectionProfile(root);
    }

    private static CredentialReference? ReadCredential(JsonElement authentication, string key)
    {
        if (!authentication.TryGetProperty(key, out var reference))
            throw new InvalidDataException("Missing SSH credential reference.");
        if (reference.ValueKind == JsonValueKind.Null) return null;
        if (reference.ValueKind != JsonValueKind.Object || Text(reference, "kind", 16) != key ||
            !Guid.TryParseExact(Text(reference, "vaultId", 36), "D", out Guid vault) || vault == Guid.Empty ||
            !Guid.TryParseExact(Text(reference, "entryId", 36), "D", out Guid entry) || entry == Guid.Empty)
            throw new InvalidDataException("Invalid SSH credential reference.");
        return new CredentialReference(vault, entry, key);
    }

    internal static bool IsComPortName(string name) =>
        name.Length is >= 4 and <= 13 && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        name[3] is >= '1' and <= '9' && name[3..].All(char.IsAsciiDigit) &&
        uint.TryParse(name.AsSpan(3), out _);

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate profile property: {property.Name}.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
                RejectDuplicateProperties(element);
        }
    }

    private static JsonElement Value(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value : throw new InvalidDataException($"Missing profile field: {name}.");

    private static JsonElement Field(JsonElement parent, string name, JsonValueKind kind)
    {
        var value = Value(parent, name);
        return value.ValueKind == kind ? value : throw new InvalidDataException($"Invalid profile field: {name}.");
    }

    private static string Text(JsonElement parent, string name, int maximum)
    {
        string value = Field(parent, name, JsonValueKind.String).GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') ||
            value.EnumerateRunes().Count() > maximum)
            throw new InvalidDataException($"Invalid profile field: {name}.");
        return value;
    }

    private static int Integer(JsonElement parent, string name, int minimum, int maximum) =>
        Field(parent, name, JsonValueKind.Number).TryGetInt32(out int value) && value >= minimum && value <= maximum
            ? value : throw new InvalidDataException($"Invalid profile field: {name}.");

    private static void Boolean(JsonElement parent, string name)
    {
        if (Value(parent, name).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Invalid profile field: {name}.");
    }

    private static string Choice(JsonElement parent, string name, params string[] choices)
    {
        string value = Text(parent, name, 64);
        return choices.Contains(value, StringComparer.Ordinal)
            ? value : throw new InvalidDataException($"Invalid profile field: {name}.");
    }

    private static void Uuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != value)
            throw new InvalidDataException("Invalid profile identifier.");
    }
}
