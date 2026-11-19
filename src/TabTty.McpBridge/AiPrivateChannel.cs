using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace TabTty.AiTransport;

internal sealed class AiPrivateChannel(PipeStream pipe, SafeProcessHandle peer, SafeProcessHandle? launcher = null)
    : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Frame> outbound = Channel.CreateBounded<Frame>(20);
    private sealed record Frame(byte[] Bytes, IDisposable? Memory);
    private const int MaxQueued = 8 * 1024 * 1024;
    // Includes three 256-scalar strings even when JSON escapes every UTF-16 code unit.
    internal const int MaxControlFrame = 16 * 1024;
    private int queued;
    private readonly object stopGate = new();
    private Task? cancellation;
    private long lastHeartbeat = Stopwatch.GetTimestamp();
    internal CancellationToken Token => lifetime.Token;

    internal static async Task HandshakeAsync(PipeStream pipe, bool server, CancellationToken token)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        if (server)
        {
            JsonElement hello = await AiFrames.ReadPrivateAsync(pipe, token).ConfigureAwait(false);
            string client = Nonce(hello, "client");
            if (hello.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
            await AiFrames.WritePrivateAsync(pipe, AiFrames.Serialize(new { version = 1, client, server = nonce }),
                token).ConfigureAwait(false);
            JsonElement confirm = await AiFrames.ReadPrivateAsync(pipe, token).ConfigureAwait(false);
            if (Nonce(confirm, "server") != nonce || Nonce(confirm, "client") != client)
                throw new InvalidDataException();
            await AiFrames.WritePrivateAsync(pipe, AiFrames.Serialize(new { version = 1, ready = true }),
                token).ConfigureAwait(false);
        }
        else
        {
            await AiFrames.WritePrivateAsync(pipe, AiFrames.Serialize(new { version = 1, client = nonce }),
                token).ConfigureAwait(false);
            JsonElement confirm = await AiFrames.ReadPrivateAsync(pipe, token).ConfigureAwait(false);
            if (confirm.GetProperty("version").GetInt32() != 1 || Nonce(confirm, "client") != nonce)
                throw new InvalidDataException();
            string serverNonce = Nonce(confirm, "server");
            await AiFrames.WritePrivateAsync(pipe, AiFrames.Serialize(new { client = nonce, server = serverNonce }),
                token).ConfigureAwait(false);
            JsonElement ready = await AiFrames.ReadPrivateAsync(pipe, token).ConfigureAwait(false);
            if (ready.GetProperty("version").GetInt32() != 1 || !ready.GetProperty("ready").GetBoolean())
                throw new InvalidDataException();
        }

        static string Nonce(JsonElement message, string property)
        {
            string? value = message.GetProperty(property).GetString();
            return value is { Length: 32 } && value.All(char.IsAsciiHexDigit) ? value : throw new InvalidDataException();
        }
    }

    internal bool TrySend(object value, bool priority = false) => TrySend(AiFrames.Serialize(value), priority);

    // A successful enqueue transfers the reservation to the writer.
    internal bool TrySend(byte[] frame, bool priority = false, IDisposable? memory = null)
    {
        if (frame.Length <= (priority ? MaxControlFrame : AiFrames.MaxFrame))
        {
            int total = Interlocked.Add(ref queued, frame.Length);
            int limit = priority ? MaxQueued : MaxQueued - 512 * 1024;
            if (total <= limit && (priority || outbound.Reader.Count < 16) &&
                outbound.Writer.TryWrite(new Frame(frame, memory))) return true;
            Interlocked.Add(ref queued, -frame.Length);
        }
        return false;
    }

    internal async Task RunAsync(Func<JsonElement, Task> receive, CancellationToken token)
    {
        using var registration = token.Register(Stop);
        Task input = ReadAsync(receive), output = WriteAsync(), health = HealthAsync();
        try
        {
            await Task.WhenAny(input, output, health).ConfigureAwait(false);
        }
        finally
        {
            Stop();
            await Task.WhenAll(input, output, health).ConfigureAwait(false);
        }
    }

    internal void Stop()
    {
        lock (stopGate)
        {
            if (cancellation is not null) return;
            // Revocation can arrive from the GUI after the peer has disconnected.
            // Close admissions before the writer drains its last owned frames.
            outbound.Writer.TryComplete();
            cancellation = lifetime.CancelAsync();
            pipe.Dispose();
        }
    }

    private async Task ReadAsync(Func<JsonElement, Task> receive)
    {
        while (!Token.IsCancellationRequested)
        {
            JsonElement message = await AiFrames.ReadPrivateAsync(pipe, Token).ConfigureAwait(false);
            if (!Healthy()) return;
            if (message.TryGetProperty("heartbeat", out var heartbeat) && heartbeat.ValueKind == JsonValueKind.True)
            {
                Interlocked.Exchange(ref lastHeartbeat, Stopwatch.GetTimestamp());
                continue;
            }
            await receive(message).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (Frame frame in outbound.Reader.ReadAllAsync(Token).ConfigureAwait(false))
            {
                try { await AiFrames.WritePrivateAsync(pipe, frame.Bytes, Token).ConfigureAwait(false); }
                finally { Release(frame); }
            }
        }
        finally { while (outbound.Reader.TryRead(out var frame)) Release(frame); }

        void Release(Frame frame)
        {
            Interlocked.Add(ref queued, -frame.Bytes.Length);
            frame.Memory?.Dispose();
        }
    }

    private bool Healthy() => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHeartbeat)) < TimeSpan.FromSeconds(20) &&
        AiPipe.IsAlive(peer) && (launcher is null || AiPipe.IsAlive(launcher));

    private async Task HealthAsync()
    {
        long sent = 0;
        while (!Token.IsCancellationRequested)
        {
            if (!Healthy()) return;
            if (sent == 0 || Stopwatch.GetElapsedTime(sent) >= TimeSpan.FromSeconds(5))
            {
                if (TrySend(new { heartbeat = true }, priority: true)) sent = Stopwatch.GetTimestamp();
            }
            await Task.Delay(250, Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Stop();
        if (cancellation is { IsCompleted: false } pending)
            _ = pending.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        else lifetime.Dispose();
    }
}
