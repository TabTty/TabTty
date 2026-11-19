using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using TabTty.AiTransport;
using TabTty.Core;

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
            launches = targets.Select(path => new InstanceLaunch(Resolve(path), [], Edit: true)).ToArray();
        }
        else if (args is ["--macro", var source, ..])
        {
            if (args.Length > 2 && args[2] != "--")
                throw new ArgumentException();
            launches = [new(Resolve(source), args.Skip(3).ToArray())];
            if (!launches[0].IsMacro) throw new ArgumentException();
        }
        else
        {
            if (args.Length > MaximumPending || args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException();
            launches = args.Select(path => new InstanceLaunch(Resolve(path), [])).ToArray();
        }
        if (!Valid(launches)) throw new ArgumentException();
        return launches;

        string Resolve(string path)
        {
            if (!OrdinaryWindowsPath.IsValid(path)) throw new ArgumentException();
            return System.IO.Path.GetFullPath(path, directory);
        }
    }

    public static bool Valid(InstanceLaunch[]? launches) => launches is { Length: <= MaximumPending } &&
        launches.All(launch => launch is { Path.Length: > 0 and <= 32760, Arguments.Length: <= 64 } &&
            ValidPath(launch.Path) &&
            (launch.HasExtension(".ttty") || launch.IsVault || (launch.IsMacro && !launch.Edit)) &&
            (launch.IsMacro ? ValidMacroText(launch.Path) : launch.Arguments.Length == 0) &&
            launch.Arguments.All(arg => arg is { Length: <= 4096 } && ValidMacroText(arg)) &&
            launch.Arguments.Sum(arg => arg.Length) <= 16384) &&
        (launches.Length <= 1 || !launches.Any(launch => launch.IsVault));

    private static bool ValidPath(string path) => System.IO.Path.IsPathFullyQualified(path) &&
        OrdinaryWindowsPath.IsValid(path) && OrdinaryWindowsPath.IsValid(System.IO.Path.GetFullPath(path));

    private bool HasExtension(string extension) =>
        string.Equals(System.IO.Path.GetExtension(Path), extension, StringComparison.OrdinalIgnoreCase);

    private static bool ValidMacroText(string value)
    {
        try { MacroLimits.Unicode(value); return true; }
        catch (MacroError) { return false; }
    }
}

internal sealed class InstanceActivation : IDisposable
{
    private const int MaximumMessage = 1024 * 1024;
    internal const string MacroRequestsProperty = "MacroTrustRequestsV2";
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();

    public InstanceActivation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string key = "TabTty-" + identity.User!.Value;
        mutex = new Mutex(true, @"Local\" + key, out bool primary);
        IsPrimary = primary;
    }

    public bool IsPrimary { get; }

    public void Listen(Func<InstanceLaunch[], Task<bool>> deliver)
    {
        CancellationToken token = stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var endpoint = new AiPipe(activation: true);
                using var pipe = endpoint.OpenListeners(1)[0];
                while (await AiPipe.AcceptAsync(pipe, token).ConfigureAwait(false))
                {
                    try
                    {
                        using var peer = endpoint.VerifyPeer(pipe, server: false, "TabTty.exe", out _);
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
                        bool macroEnvelope = root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty(MacroRequestsProperty, out _);
                        if (macroEnvelope && root.TryGetProperty(MacroRequestsProperty, out var macros) &&
                            macros.ValueKind == JsonValueKind.Array && macros.GetArrayLength() is > 0 and <= InstanceLaunch.MaximumPending)
                        {
                            launches = macros.Deserialize<InstanceLaunch[]>();
                        }
                        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("EditorRequests", out var editors) &&
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
                        bool accepted = launches is not null && InstanceLaunch.Valid(launches) &&
                            launches.Any(launch => launch.IsMacro) == macroEnvelope &&
                            await deliver(launches).ConfigureAwait(false);
                        await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, deadline.Token)
                            .ConfigureAwait(false);
                        // The forwarder closes its end after reading the acknowledgement.
                        // Disconnecting sooner could discard the unread byte.
                        await pipe.ReadAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or
                        Win32Exception or UnauthorizedAccessException or OperationCanceledException) { }
                    finally { pipe.Disconnect(); }
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or
                Win32Exception or UnauthorizedAccessException) { }
        }, token);
    }

    public async Task<bool> ForwardAsync(InstanceLaunch[] paths)
    {
        if (!InstanceLaunch.Valid(paths)) return false;
        byte[] message = EncodeRequests(paths);
        if (message.Length > MaximumMessage) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = new AiPipe(activation: true);
        using var pipe = await endpoint.ConnectAsync(deadline.Token).ConfigureAwait(false);
        using var peer = endpoint.VerifyPeer(pipe, server: true, "TabTty.exe", out _);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await pipe.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(message, deadline.Token).ConfigureAwait(false);
        byte[] reply = new byte[1];
        await pipe.ReadExactlyAsync(reply, deadline.Token).ConfigureAwait(false);
        return reply[0] == 1;
    }

    internal static byte[] EncodeRequests(InstanceLaunch[] paths)
    {
        // Older receivers must reject operations for which their behavior differs.
        if (paths.Any(path => path.IsMacro))
            return JsonSerializer.SerializeToUtf8Bytes(new { MacroTrustRequestsV2 = paths });
        if (paths.Any(path => path.Edit || path.IsVault))
            return JsonSerializer.SerializeToUtf8Bytes(new { EditorRequests = paths });
        return JsonSerializer.SerializeToUtf8Bytes(paths.Select(path => path.Path).ToArray());
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
