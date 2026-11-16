using System.Net.Sockets;

namespace TabTty.Core;

internal interface ISessionWire : IDisposable
{
    TerminalReceiveSource Source => TerminalReceiveSource.Data;
    void Acknowledge(int count) { }
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    ValueTask CloseAsync() { Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class TcpSessionWire(TcpClient client) : ISessionWire
{
    private readonly NetworkStream stream = client.GetStream();

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        stream.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        stream.WriteAsync(buffer, cancellationToken);

    public void Dispose() => client.Dispose();
}
