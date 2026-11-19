using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabTty.AiTransport;

internal static class AiFrames
{
    internal const int MaxFrame = 2 * 1024 * 1024;
    internal const int MaxResult = 1024 * 1024;
    private const int MaxJsonTokens = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    internal static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    internal static object ToolResult(JsonElement body, bool modern) => new
    {
        resultType = modern ? "complete" : null,
        isError = body.TryGetProperty("status", out var status) && status.ValueEquals("error"),
        structuredContent = body,
        content = new[] { new { type = "text", text = body.GetRawText() } },
    };

    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes) => Parse(bytes, out _);

    private static JsonElement Parse(ReadOnlyMemory<byte> bytes, out int tokens)
    {
        // Include container tokens in both the nesting/token check and DOM accounting.
        var reader = new Utf8JsonReader(bytes.Span, new JsonReaderOptions { MaxDepth = 32 });
        tokens = 0;
        while (reader.Read())
            if (++tokens > MaxJsonTokens) throw new JsonException();
        _ = StrictUtf8.GetCharCount(bytes.Span);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        Validate(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                _ = StrictUtf8.GetByteCount(member.Name);
                if (!names.Add(member.Name)) throw new JsonException();
                Validate(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var member in value.EnumerateArray()) Validate(member);
        else if (value.ValueKind == JsonValueKind.String)
            _ = StrictUtf8.GetByteCount(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Number &&
            (!value.TryGetDouble(out double number) || !double.IsFinite(number))) throw new JsonException();
    }

    internal sealed record PrivateFrame(JsonElement Message, int Length, long RetainedBytes);

    internal static async Task<JsonElement> ReadPrivateAsync(Stream stream, CancellationToken token) =>
        (await ReadPrivateFrameAsync(stream, token).ConfigureAwait(false)).Message;

    internal static async Task<PrivateFrame> ReadPrivateFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        using var deadline = Deadline(token);
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxFrame) throw new InvalidDataException();
        byte[] body = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
        JsonElement message = Parse(body, out int tokens);
        // The retained clone owns the UTF-8 frame and a token index, including
        // structural rows. Leave margin for object and array overhead.
        return new PrivateFrame(message, length, length + 24L * tokens + 512);
    }

    internal static async Task WritePrivateAsync(Stream stream, byte[] body, CancellationToken token)
    {
        if (body.Length is < 1 or > MaxFrame) throw new InvalidDataException();
        using var deadline = Deadline(token);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    internal static CancellationTokenSource Deadline(CancellationToken token, int seconds = 20)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        return deadline;
    }

    internal sealed class StdioReader(Stream input)
    {
        private readonly byte[] buffer = new byte[16 * 1024];
        private int start, end;

        internal async Task<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken token)
        {
            var frame = new ArrayBufferWriter<byte>();
            if (start == end && !await FillAsync(token).ConfigureAwait(false)) return null;
            using var deadline = Deadline(token);
            while (true)
            {
                int newline = Array.IndexOf(buffer, (byte)10, start, end - start);
                int stop = newline < 0 ? end : newline;
                int count = stop - start;
                if (count > MaxFrame - frame.WrittenCount) throw new InvalidDataException();
                frame.Write(buffer.AsSpan(start, count));
                start = stop;
                if (newline >= 0)
                {
                    start++;
                    return frame.WrittenMemory;
                }
                if (!await FillAsync(deadline.Token).ConfigureAwait(false)) throw new EndOfStreamException();
            }
        }

        private async Task<bool> FillAsync(CancellationToken token)
        {
            start = 0;
            // Redirected Windows stdin may already be blocked in an uncancellable
            // read. On timeout the caller ends this attachment and never reuses the buffer.
            end = await input.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
            return end != 0;
        }
    }
}
