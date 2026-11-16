using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TabTty.App;

internal sealed record InstanceLaunch(string Path, string[] Arguments, bool Edit = false)
{
    public const int MaximumPending = 64;
    public bool IsMacro => HasExtension(".tttym");
    public bool IsVault => HasExtension(".tttykey");

    public static InstanceLaunch[] Parse(string[] args)
    {
        string directory = Directory.GetCurrentDirectory();
        InstanceLaunch[] launches;
        if (args is ["--edit", .. var targets])
        {
            if (targets.Length is 0 or > MaximumPending) throw new ArgumentException();
            launches = targets.Select(path => new InstanceLaunch(System.IO.Path.GetFullPath(path, directory), [], Edit: true)).ToArray();
        }
        else if (args is ["--macro", var source, ..])
        {
            if (args.Length > 2 && args[2] != "--")
                throw new ArgumentException();
            launches = [new(System.IO.Path.GetFullPath(source, directory), args.Skip(3).ToArray())];
            if (!launches[0].IsMacro) throw new ArgumentException();
        }
        else
        {
            if (args.Length > MaximumPending || args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException();
            launches = args.Select(path => new InstanceLaunch(System.IO.Path.GetFullPath(path, directory), [])).ToArray();
        }
        if (!Valid(launches)) throw new ArgumentException();
        return launches;
    }

    public static bool Valid(InstanceLaunch[]? launches) => launches is { Length: <= MaximumPending } &&
        launches.All(launch => launch is { Path.Length: > 0 and <= 32760, Arguments.Length: <= 64 } &&
            ValidPath(launch.Path) &&
            (launch.HasExtension(".ttty") || launch.IsVault || (launch.IsMacro && !launch.Edit)) &&
            (launch.IsMacro || launch.Arguments.Length == 0) &&
            launch.Arguments.All(arg => arg is { Length: <= 4096 } && !arg.Contains('\0')) &&
            launch.Arguments.Sum(arg => arg.Length) <= 16384) &&
        (launches.Length <= 1 || !launches.Any(launch => launch.IsVault));

    private static bool ValidPath(string path) => System.IO.Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
        !path.AsSpan().ContainsAny('\0', '*', '?') &&
        !path.AsSpan(path.Length > 1 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? 2 : 0).Contains(':');

    private bool HasExtension(string extension) =>
        string.Equals(System.IO.Path.GetExtension(Path), extension, StringComparison.OrdinalIgnoreCase);
}

internal sealed class InstanceActivation : IDisposable
{
    private const int MaximumMessage = 1024 * 1024;
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();

    public InstanceActivation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string key = "TabTty-" + identity.User!.Value;
        using Process process = Process.GetCurrentProcess();
        pipeName = $"{key}-{process.SessionId}";
        mutex = new Mutex(true, @"Local\" + key, out bool primary);
        IsPrimary = primary;
    }

    public bool IsPrimary { get; }

    public void Listen(Func<InstanceLaunch[], Task<bool>> deliver)
    {
        CancellationToken token = stop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    byte[] header = new byte[4];
                    await pipe.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is <= 0 or > MaximumMessage) continue;
                    byte[] message = new byte[length];
                    await pipe.ReadExactlyAsync(message, deadline.Token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 8 });
                    JsonElement root = document.RootElement;
                    InstanceLaunch[]? launches = null;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("EditorRequests", out var editors) &&
                        editors.ValueKind == JsonValueKind.Array && editors.GetArrayLength() is > 0 and <= InstanceLaunch.MaximumPending)
                    {
                        launches = editors.Deserialize<InstanceLaunch[]>();
                        if (launches?.Any(launch => launch is not null && !launch.Edit && !launch.IsVault) == true)
                            launches = null;
                    }
                    else if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() <= InstanceLaunch.MaximumPending)
                    {
                        launches = root.GetArrayLength() == 0 || root[0].ValueKind == JsonValueKind.String
                            ? root.Deserialize<string[]>()?.Select(path => new InstanceLaunch(path, [])).ToArray()
                            : root.Deserialize<InstanceLaunch[]>();
                    }
                    bool accepted = InstanceLaunch.Valid(launches) && await deliver(launches!).ConfigureAwait(false);
                    await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
                {
                    if (token.IsCancellationRequested) return;
                }
            }
        }, token);
    }

    public async Task<bool> ForwardAsync(InstanceLaunch[] paths)
    {
        if (!InstanceLaunch.Valid(paths)) return false;
        // Older readers reject the object shape instead of interpreting Edit as Open.
        byte[] message = paths.Any(path => path.Edit || path.IsVault)
            ? JsonSerializer.SerializeToUtf8Bytes(new { EditorRequests = paths })
            : paths.Any(path => path.IsMacro) ? JsonSerializer.SerializeToUtf8Bytes(paths)
            : JsonSerializer.SerializeToUtf8Bytes(paths.Select(path => path.Path).ToArray());
        if (message.Length > MaximumMessage) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await pipe.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(message, deadline.Token).ConfigureAwait(false);
        byte[] reply = new byte[1];
        await pipe.ReadExactlyAsync(reply, deadline.Token).ConfigureAwait(false);
        return reply[0] == 1;
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
