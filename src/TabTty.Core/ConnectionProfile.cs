using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabTty.Core;

public sealed record JapaneseSettings(string KanjiIn = "B", string KanjiOut = "B",
    string ReceiveKana = "eightBit", string TransmitKana = "eightBit");

public sealed record SerialSettings(string PortName, uint BaudRate = 9600, int DataBits = 8,
    string Parity = "none", int StopBits = 1, string FlowControl = "none", bool Dtr = true, bool Rts = true);

public sealed record CredentialReference(string Name, string Kind);

public sealed class ConnectionProfile
{
    private const int MaximumBytes = 1024 * 1024;
    internal const int MaximumGroupNameLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    private readonly JsonElement document;
    private byte[]? sourceBytes;
    private string? sourcePath;
    private string? unsavedName;
    private JsonElement Connection => document.GetProperty("connection");
    private JsonElement Terminal => document.GetProperty("terminal");
    private JsonElement Logging => document.GetProperty("logging");

    private ConnectionProfile(JsonElement document, string? groupName, LogCollisionPolicy logCollision)
    {
        this.document = document;
        GroupName = groupName;
        LogCollision = logCollision;
    }

    public string DisplayName => sourcePath is null ? unsavedName ?? "" : Path.GetFileNameWithoutExtension(sourcePath);
    internal string OpenedPath => sourcePath ?? throw new InvalidOperationException("Profile has no file path.");
    internal byte[] SnapshotBytes => sourceBytes ?? throw new InvalidOperationException("Profile has no verified file revision.");
    internal string? RetainedSaveBackup { get; private set; }
    public string? GroupName { get; }
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
    public bool SshAllowsKeyboardInteractive => Connection.GetProperty("authentication")
        .GetProperty("allowKeyboardInteractive").GetBoolean();
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
    public string LogDirectory => Logging.TryGetProperty("directory", out var value) ? value.GetString()! : "";
    public string LogFilenameTemplate => Logging.TryGetProperty("filenameTemplate", out var value) ? value.GetString()! : "";
    public LogCollisionPolicy LogCollision { get; }
    public Guid? HighlightSetId => Terminal.GetProperty("highlightSetId").Deserialize<Guid?>();
    public string? FontFamily => Terminal.GetProperty("font").GetProperty("family").Deserialize<string?>();
    public double? FontSizePoints => Terminal.GetProperty("font").GetProperty("sizePoints").Deserialize<double?>();
    public string ToEditorJson() => JsonSerializer.Serialize(document, SaveOptions);

    public void ValidateEditorSettings()
    {
        if (!string.IsNullOrWhiteSpace(LogDirectory))
            _ = OrdinaryWindowsPath.Resolve(LogDirectory, sourcePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(sourcePath)!);
        if (!string.IsNullOrWhiteSpace(LogFilenameTemplate)) RecordingNameTemplate.Parse(LogFilenameTemplate, macro: true);
    }

    public ConnectionProfile Edit(string json)
    {
        if (json.Length > MaximumBytes || StrictUtf8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Connection profile exceeds 1 MiB.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        ConnectionProfile revision = Validate(parsed.RootElement.Clone());
        if (revision.Id != Id) throw new InvalidDataException("An edit must preserve the profile ID.");
        revision.unsavedName = unsavedName;
        revision.sourcePath = sourcePath;
        revision.sourceBytes = sourceBytes;
        return revision;
    }

    public ConnectionProfile Duplicate()
    {
        JsonObject copy = JsonNode.Parse(document.GetRawText())!.AsObject();
        copy["profileId"] = Guid.NewGuid();
        var duplicate = Validate(JsonSerializer.SerializeToElement(copy));
        duplicate.unsavedName = DisplayName;
        return duplicate;
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
        IReadOnlyList<string>? methods = null, CredentialReference? privateKey = null,
        bool allowKeyboardInteractive = true) => Create(name, new
        {
            type = "ssh", waitSeconds = 30, host, port, username,
            authentication = new
            {
                methods = methods ?? ["password"],
                allowKeyboardInteractive,
                privateKey = privateKey?.Name,
                password = password?.Name,
            },
            liveness = new { enabled = false, intervalSeconds = 30, unansweredWindows = 3 },
        }, "cr", "del", "off", recordRaw, receiveEncoding, transmitEncoding, japanese, ambiguousWidth,
            recordText, recordTerminal, highlightSetId);

    private static ConnectionProfile Create(string name, object connection, string enter, string backspace,
        string localEcho, bool recordRaw, string receiveEncoding, string? transmitEncoding,
        JapaneseSettings? japanese, int ambiguousWidth, bool recordText, bool recordTerminal, Guid? highlightSetId)
    {
        var profile = Validate(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            requiredFeatures = Array.Empty<string>(),
            profileId = Guid.NewGuid().ToString("D"),
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
            logging = new { raw = recordRaw, text = recordText, terminal = recordTerminal, directory = "",
                collision = LogCollisionPolicies.Format(LogCollisionPolicy.Error) },
        }));
        profile.unsavedName = name;
        return profile;
    }

    public static async Task<ConnectionProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await ApplicationResources.FilePreparations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadPreparedAsync(path, cancellationToken).ConfigureAwait(false); }
        finally { ApplicationResources.FilePreparations.Release(); }
    }

    internal string TrustVersion => "1:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(sourceBytes ??
            throw new InvalidOperationException("Profile has no verified file revision.")));

    internal string? LocalOpeningLocation { get; private set; }

    internal static async Task<ConnectionProfile?> TryLoadLocalAsync(string path, CancellationToken token)
    {
        string source = OpeningPath(path);
        await ApplicationResources.FilePreparations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var handle = LocalMacroFile.TryOpen(source);
                if (handle is null) return null;
                string location = LocalMacroFile.OpenedLocation(handle, source);
                await using var stream = new FileStream(handle, FileAccess.Read);
                var profile = ParseProfile(location, await ReadBytesAsync(stream, token).ConfigureAwait(false));
                profile.LocalOpeningLocation = location;
                return profile;
            }, token).ConfigureAwait(false);
        }
        finally { ApplicationResources.FilePreparations.Release(); }
    }

    private static async Task<ConnectionProfile> LoadPreparedAsync(string path, CancellationToken cancellationToken)
    {
        string source = OpeningPath(path);
        byte[] bytes = await ReadBytesAsync(source, cancellationToken).ConfigureAwait(false);
        return ParseProfile(source, bytes);
    }

    private static ConnectionProfile ParseProfile(string source, byte[] bytes)
    {
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
        return await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBytesAsync(FileStream stream, CancellationToken cancellationToken)
    {
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
        SaveWithCompatibilityAsync(OpeningPath(path), null, cancellationToken);

    internal ProfileSaveOperation BeginSaveNew(string path, Action<string, string, string?>? issue = null,
        Action<string>? validateStage = null) =>
        BeginSave(OpeningPath(path), null, CancellationToken.None, issue, validateStage);

    private static string OpeningPath(string path) => OrdinaryWindowsPath.Resolve(path);

    public Task SaveEditAsync(CancellationToken cancellationToken = default)
    {
        if (sourcePath is null || sourceBytes is null)
            throw new InvalidOperationException("Load a saved profile before editing it.");
        return SaveWithCompatibilityAsync(sourcePath, sourceBytes, cancellationToken);
    }

    internal ProfileSaveOperation BeginSaveEdit(Action<string, string, string?>? issue = null, Action<string>? validateStage = null)
    {
        if (sourcePath is null || sourceBytes is null)
            throw new InvalidOperationException("Load a saved profile before editing it.");
        return BeginSave(sourcePath, sourceBytes, CancellationToken.None, issue, validateStage);
    }

    internal bool IsSavedAt(string path) => sourcePath == path;

    internal async Task<FileObjectIdentity?> CheckSavedRevisionAsync(string location, CancellationToken token)
    {
        if (sourceBytes is null) return null;
        await using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var identity = FileObjectIdentity.Read(stream.SafeFileHandle);
        byte[] bytes = await ReadBytesAsync(stream, token).ConfigureAwait(false);
        return bytes.AsSpan().SequenceEqual(sourceBytes) ? identity : null;
    }

    internal void MoveSavedRevision(string destination)
    {
        if (sourcePath is null || sourceBytes is null)
            throw new InvalidOperationException("Load a saved profile before moving it.");
        string target = OpeningPath(destination);
        using var reservation = VerifiedFilePublication.ReserveNamespace(
            string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase) ? [sourcePath] : [sourcePath, target], 0);
        FileObjectIdentity identity;
        using (var source = File.OpenRead(sourcePath))
            identity = FileObjectIdentity.Read(source.SafeFileHandle);
        if (!FileObjectIdentity.MoveIfSame(sourcePath, identity, target, replace: false, expectedBytes: sourceBytes))
            throw new IOException("The saved profile changed before it could be renamed.");
        sourcePath = target;
    }

    private async Task SaveWithCompatibilityAsync(string destination, byte[]? original, CancellationToken cancellationToken) =>
        await BeginSave(destination, original, cancellationToken).ObserveAsync().ConfigureAwait(false);

    private ProfileSaveOperation BeginSave(string destination, byte[]? original, CancellationToken cancellationToken,
        Action<string, string, string?>? issue = null, Action<string>? validateStage = null)
    {
        ValidateEditorSettings();
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(document, SaveOptions);
        if (serialized.Length > MaximumBytes) throw new InvalidDataException("Connection profile exceeds 1 MiB.");
        var request = new FilePublicationRequest(destination, serialized,
            original is null ? null : System.Security.Cryptography.SHA256.HashData(original),
            original is null ? null : destination + ".bak", MaximumBytes,
            Validate: prepared =>
            {
                _ = ParseProfile(destination, ReadBytesAsync(prepared, cancellationToken).GetAwaiter().GetResult());
                validateStage?.Invoke(prepared);
            }, CreateDirectory: original is null, Issue: issue, RemovePreservationOnVerified: original is not null);
        var admitted = VerifiedFilePublication.Begin(request, cancellationToken);
        return new(admitted, AdoptSaveAsync(admitted, destination, serialized));
    }

    private async Task AdoptSaveAsync(FilePublicationOperation admitted, string destination, byte[] serialized)
    {
        var operation = await admitted.ObserveAsync().ConfigureAwait(false);
        if (operation.Status != FilePublicationStatus.Verified)
            throw new FilePublicationException(operation.Status == FilePublicationStatus.Unchanged
                ? "The profile save did not change the destination."
                : "The profile save or original backup could not be verified.", operation);
        sourcePath = destination;
        sourceBytes = serialized;
        RetainedSaveBackup = operation.CleanupError is null ? null : operation.PreservationPath;
    }

    internal sealed class ProfileSaveOperation(FilePublicationOperation admitted, Task adopted)
    {
        internal Guid Id => admitted.Id;
        internal Task ObserveAsync(CancellationToken observation = default) => adopted.WaitAsync(observation);
    }

    internal static Task<ConnectionProfile?> ReconcileSaveAsync(string path, Guid? operationId = null) => Task.Run(() =>
    {
        ConnectionProfile? committed = null;
        var outcome = VerifiedFilePublication.Reconcile(OpeningPath(path), validated =>
        {
            committed = ParseProfile(validated, ReadBytesAsync(validated, CancellationToken.None).GetAwaiter().GetResult());
        }, operationId);
        if (outcome is { Status: not FilePublicationStatus.Verified })
            throw new FilePublicationException("The profile save or original backup could not be verified.", outcome);
        if (committed is not null && outcome?.CleanupError is not null)
            committed.RetainedSaveBackup = outcome.PreservationPath;
        return committed;
    });

    private static ConnectionProfile Validate(JsonElement root)
    {
        RejectDuplicateProperties(root);
        if (Integer(root, "schemaVersion", 1, 1) != 1 ||
            Field(root, "requiredFeatures", JsonValueKind.Array).GetArrayLength() != 0)
        {
            throw new InvalidDataException("Unsupported profile version or required feature.");
        }

        Uuid(Text(root, "profileId", 36));
        if (root.TryGetProperty("displayName", out _))
            throw new InvalidDataException("The retired displayName profile property is unsupported.");
        string? groupName = root.TryGetProperty("groupName", out var group) && group.ValueKind != JsonValueKind.Null
            ? NormalizeGroupName(Field(root, "groupName", JsonValueKind.String).GetString()) : null;
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
            if (methods.GetArrayLength() is < 1 or > 2 || methods.EnumerateArray().Any(static method =>
                method.ValueKind != JsonValueKind.String || method.GetString() is not ("publicKey" or "password")) ||
                methods.EnumerateArray().Select(static method => method.GetString()).Distinct().Count() != methods.GetArrayLength())
                throw new InvalidDataException("Invalid SSH authentication methods.");
            Boolean(authentication, "allowKeyboardInteractive");
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
        OptionalLogText("directory", 32760);
        OptionalLogText("filenameTemplate", RecordingNameTemplate.MaximumLength);
        LogCollisionPolicy policy = LogCollisionPolicy.Error;
        if (logging.TryGetProperty("collision", out var collision) &&
            (collision.ValueKind != JsonValueKind.String || !LogCollisionPolicies.TryParse(collision.GetString(), out policy)))
            throw new InvalidDataException("Invalid profile field: collision.");
        return new ConnectionProfile(root, groupName, policy);

        void OptionalLogText(string key, int maximum)
        {
            if (logging.TryGetProperty(key, out var entry) &&
                (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } value ||
                 value.Length > maximum || value.Contains('\0')))
                throw new InvalidDataException($"Invalid profile field: {key}.");
        }
    }

    internal static string? NormalizeGroupName(string? value)
    {
        string? name = value?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.EnumerateRunes().Count() > MaximumGroupNameLength || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid profile field: groupName.");
        return name;
    }

    private static CredentialReference? ReadCredential(JsonElement authentication, string key)
    {
        if (!authentication.TryGetProperty(key, out var reference))
            throw new InvalidDataException("Missing SSH credential reference.");
        if (reference.ValueKind == JsonValueKind.Null) return null;
        string? name = reference.ValueKind == JsonValueKind.String ? reference.GetString() : null;
        if (!VaultPayload.IsValidName(name)) throw new InvalidDataException("Invalid SSH credential name.");
        return new CredentialReference(name!, key);
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
