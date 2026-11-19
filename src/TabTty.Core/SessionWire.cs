using System.Net.Sockets;

namespace TabTty.Core;

internal interface ISessionWire : IDisposable
{
    TerminalReceiveSource Source => TerminalReceiveSource.Data;
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This transport delivers terminal payload through a consumer.");
    async ValueTask<int> DeliverPayloadAsync(Memory<byte> buffer, TerminalPayloadConsumer consume, CancellationToken token)
    {
        int count = await ReadAsync(buffer, token).ConfigureAwait(false);
        if (count != 0) await consume(Source, buffer[..count].ToArray(), CancellationToken.None).ConfigureAwait(false);
        return count;
    }
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    async ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }
    ValueTask CloseAsync() { Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class TcpSessionWire(TcpClient client) : ISessionWire
{
    private readonly NetworkStream stream = client.GetStream();

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        stream.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        stream.WriteAsync(buffer, cancellationToken);

    public ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        client.Client.SendAsync(buffer, SocketFlags.None, cancellationToken);

    public void Dispose() => client.Dispose();
}
