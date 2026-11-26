using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace TabTty.Core;

internal sealed class PasteWriteException(Exception cause, int definiteBytes, TransportSubmission submission) :
    IOException(submission == TransportSubmission.NotIssued ? cause.Message :
        "The terminal may have received part of the attempted paste unit.", cause)
{
    internal int DefiniteBytes { get; } = definiteBytes;
    internal TransportSubmission Submission { get; } = submission;
}

internal enum ReceiveOutcome { Delivered, Completed, Ended }

internal delegate Task TerminalPayloadConsumer(TerminalReceiveSource source, byte[] payload, CancellationToken token);

internal readonly record struct TransportStatus(bool Ready, bool ReceiveEnded, Exception? DeliveryFailure, bool ReleasePending);

internal enum TransportSubmission { NotIssued, Submitted, Partial, Uncertain }
internal class TransportAdmissionRejectedException(string reason) : InvalidOperationException(reason);
internal sealed record InputCutoff(CancellationToken Token, Func<bool> IsStopped,
    Func<int, int> BoundaryAtOrAfter, Func<int, byte[]>? ClosePrefix);
internal readonly record struct TransportSendResult(TransportSubmission Status, int DefiniteBytes, Exception? Error,
    SshSendState? Ssh = null);
internal enum GeometryNotification { NoNotification, Submitted, Superseded, Failed }
internal readonly record struct TransportGeometryResult(GeometryNotification Status, Exception? Error = null);

internal sealed class SessionTransport
{
    internal readonly record struct GeometryUpdate(long Revision, int Columns, int Rows, byte[]? TelnetNotice, bool Ssh);
    private ISessionWire? wire;
    private readonly ConnectionProfile profile;
    private ISessionWire ConnectedWire => wire ?? throw new IOException("The terminal transport has not opened.");
    private readonly TerminalPayloadConsumer consume;
    private readonly Func<bool, CancellationToken, Task> remoteEchoChanged;
    private readonly Func<byte[], CancellationToken, Task> receiveEof;
    private readonly TelnetProtocol? telnet;
    private readonly ResourceReservation receiveMemory = ApplicationResources.Receive.Reserve(64 * 1024);
    private byte[] receiveBuffer = new byte[8192];
    private readonly ResourceQuota controlBytes = new(64 * 1024, ApplicationResources.Receive, collectOnPressure: false);
    private readonly SemaphoreSlim reader = new(1);
    private readonly SemaphoreSlim writer = new(1);
    private readonly SemaphoreSlim geometryWriter = new(1);
    private long geometryRevision;
    private readonly object protocolGate = new();
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource opening = new();
    private readonly Func<SerialSettings, ISessionWire> openSerial;
    private Task? start, close;
    private Task lateOpenRelease = Task.CompletedTask;
    private bool eofDelivered;
    private Exception? deliveryFailure;
    private readonly TaskCompletionSource deliverySettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource actualRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task DeliverySettled => deliverySettled.Task;
    internal Task ActualRelease => actualRelease.Task;
    internal TransportStatus Status => new(start?.IsCompletedSuccessfully == true && close is null, Volatile.Read(ref eofDelivered),
        Volatile.Read(ref deliveryFailure), !ActualRelease.IsCompleted);

    internal SessionTransport(ISessionWire? wire, ConnectionProfile profile, (int Columns, int Rows) grid,
        TerminalPayloadConsumer consume, Func<bool, CancellationToken, Task> remoteEchoChanged,
        Func<byte[], CancellationToken, Task> receiveEof, Func<SerialSettings, ISessionWire>? openSerial = null)
    {
        this.wire = wire;
        this.profile = profile;
        this.consume = consume;
        this.remoteEchoChanged = remoteEchoChanged;
        this.receiveEof = receiveEof;
        this.openSerial = openSerial ?? SerialSessionWire.Open;
        if (profile.Type == "telnet")
            telnet = new TelnetProtocol(profile.AdvertisedType, (ushort)grid.Columns, (ushort)grid.Rows, profile.InitiateBinary);
    }

    internal bool IsTelnet => telnet is not null;

    internal bool IsSsh => profile.Type == "ssh" || wire is SshSessionWire;
    internal bool IsSerial => profile.Type == "serial" || wire is SerialSessionWire;
    internal bool CanSendAfterEof => wire is SshSessionWire { PeerClosed.IsCompleted: false };
    internal Task PeerClosed => wire is SshSessionWire ssh ? ssh.PeerClosed : Task.CompletedTask;
    internal Exception? PeerCloseFailure => (wire as SshSessionWire)?.PeerCloseFailure;
    internal string? SshDetails => (wire as SshSessionWire)?.Details;
    internal bool CanSetDtr => wire is SerialSessionWire { CanSetDtr: true };
    internal bool CanSetRts => wire is SerialSessionWire { CanSetRts: true };
    private SerialSessionWire Serial => wire as SerialSessionWire ?? throw new InvalidOperationException("A COM session is required.");

    internal Task<SftpClient> OpenTransferAsync(string encoding, CancellationToken token) =>
        wire is SshSessionWire ssh ? ssh.OpenTransferAsync(encoding, token) :
            throw new InvalidOperationException("An SSH session is required.");

    internal Task SendSerialBreakAsync(CancellationToken token) => Serial.SendBreakAsync(token);
    internal void SetSerialLine(bool dtr, bool enabled) => Serial.SetLine(dtr, enabled);

    internal Task StartAsync(CancellationToken attempt, SshInteraction? ssh = null,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? getGrid = null,
        TerminalPayloadConsumer? startupPayload = null, CancellationToken sessionLifetime = default)
    {
        lock (lifecycle)
        {
            if (start is not null) return start;
            if (close is not null) return Task.FromException(new IOException("The terminal transport has closed."));
            return start = StartCoreAsync(attempt, ssh, getGrid, startupPayload, sessionLifetime);
        }
    }

    private async Task StartCoreAsync(CancellationToken attempt, SshInteraction? ssh,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? getGrid,
        TerminalPayloadConsumer? startupPayload, CancellationToken sessionLifetime)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt, opening.Token);
        attempt = deadline.Token;
        wire ??= profile.Type switch
        {
            "ssh" => await SshSessionWire.OpenAsync(profile, ssh!, getGrid!, startupPayload!, attempt, sessionLifetime).ConfigureAwait(false),
            "serial" => await OpenSerialAsync(profile.Serial, attempt).ConfigureAwait(false),
            _ => await OpenTcpAsync(profile, attempt).ConfigureAwait(false),
        };
        if (!IsSsh) attempt.ThrowIfCancellationRequested();
        if (telnet is null) return;
        byte[] initial;
        lock (protocolGate) initial = telnet.Start();
        await WriteAsync(initial, attempt).ConfigureAwait(false);
    }

    private static async Task<ISessionWire> OpenTcpAsync(ConnectionProfile profile, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(profile.Host.Trim('[', ']'), profile.Port, token).ConfigureAwait(false);
            return new TcpSessionWire(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<ISessionWire> OpenSerialAsync(SerialSettings serial, CancellationToken token)
    {
        await ApplicationResources.FilePreparations.WaitAsync(token).ConfigureAwait(false);
        var result = new TaskCompletionSource<ISessionWire>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adoption = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lateOpenRelease = Task.Run(async () =>
        {
            try
            {
                ISessionWire candidate;
                try
                {
                    token.ThrowIfCancellationRequested();
                    candidate = openSerial(serial);
                }
                catch (Exception error)
                {
                    result.SetException(error);
                    _ = result.Task.Exception;
                    return;
                }
                result.SetResult(candidate);
                if (!await adoption.Task.ConfigureAwait(false)) await candidate.CloseAsync().ConfigureAwait(false);
            }
            finally { ApplicationResources.FilePreparations.Release(); }
        });
        try
        {
            ISessionWire candidate = await result.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            adoption.SetResult(true);
            return candidate;
        }
        catch
        {
            adoption.SetResult(false);
            throw;
        }
    }

    internal async Task<ReceiveOutcome> ReceiveAsync(CancellationToken token)
    {
        await reader.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (eofDelivered) return ReceiveOutcome.Ended;
            var connected = ConnectedWire;
            int count = telnet is null
                ? await connected.DeliverPayloadAsync(receiveBuffer, consume, token).ConfigureAwait(false)
                : await connected.ReadAsync(receiveBuffer, token).ConfigureAwait(false);
            if (count == 0)
            {
                await receiveEof(CompleteReceive(), CancellationToken.None).ConfigureAwait(false);
                eofDelivered = true;
                deliverySettled.TrySetResult();
                return ReceiveOutcome.Completed;
            }

            if (telnet is not null)
            {
                List<(byte[] Before, bool Echo)>? transitions = null;
                (byte[] body, byte[] response) received;
                lock (protocolGate)
                    received = telnet.Receive(receiveBuffer.AsSpan(0, count), (before, echo) =>
                    {
                        transitions ??= [];
                        if (transitions.Count == 256)
                            throw new ResourceLimitException("The Telnet receive metadata budget was exhausted.");
                        transitions.Add((before, echo));
                    });
                if (received.response.Length != 0) SubmitTelnetControl(received.response);
                if (transitions is not null)
                {
                    foreach (var (before, echo) in transitions)
                    {
                        if (before.Length != 0) await consume(TerminalReceiveSource.Data, before, CancellationToken.None).ConfigureAwait(false);
                        await remoteEchoChanged(echo, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                if (received.body.Length != 0)
                    await consume(TerminalReceiveSource.Data, received.body, CancellationToken.None).ConfigureAwait(false);
            }
            return ReceiveOutcome.Delivered;
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            deliveryFailure = error;
            eofDelivered = true;
            _ = DisconnectAsync();
            throw;
        }
        finally { reader.Release(); }
    }

    private void SubmitTelnetControl(byte[] packet)
    {
        // Retain a bounded reservation independently of the receive buffer.
        var ownership = controlBytes.Reserve(packet.Length);
        _ = SendTelnetControlAsync(packet, ownership);
    }

    private async Task SendTelnetControlAsync(byte[] packet, ResourceReservation ownership)
    {
        using (ownership)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(profile.WaitSeconds)))
        {
            try { await WriteAsync(packet, deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                deliveryFailure ??= error;
                _ = DisconnectAsync();
            }
        }
    }

    internal byte[] CompleteReceive()
    {
        lock (protocolGate) return telnet?.CompleteReceive() ?? [];
    }

    internal byte[] EncodeInput(byte[] bytes)
    {
        lock (protocolGate) return telnet?.EncodeInput(bytes) ?? bytes;
    }

    internal ValueTask TelnetCommandAsync(bool isBreak, CancellationToken token)
    {
        if (telnet is null) throw new InvalidOperationException("This command requires Telnet.");
        return WriteAsync(isBreak ? telnet.Break() : telnet.AreYouThere(), token);
    }

    internal GeometryUpdate CommitGeometry(int columns, int rows)
    {
        byte[]? notice = null;
        long revision = Interlocked.Increment(ref geometryRevision);
        if (telnet is not null)
        {
            lock (protocolGate) notice = telnet.SetWindowSize((ushort)columns, (ushort)rows);
            if (notice.Length == 0) notice = null;
        }
        return new(revision, columns, rows, notice, wire is SshSessionWire);
    }

    internal async Task<TransportGeometryResult> ApplyGeometryAsync(GeometryUpdate geometry, CancellationToken token)
    {
        if (geometry.TelnetNotice is null && !geometry.Ssh) return new(GeometryNotification.NoNotification);
        // SSH geometry is protocol control and must not wait for application
        // data to acquire channel credit.
        var order = geometry.Ssh ? geometryWriter : writer;
        try
        {
            await order.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (geometry.Revision != Volatile.Read(ref geometryRevision)) return new(GeometryNotification.Superseded);
                if (geometry.TelnetNotice is { } notice) await ConnectedWire.WriteAsync(notice, token).ConfigureAwait(false);
                else await ((SshSessionWire)ConnectedWire).ResizeAsync(geometry.Columns, geometry.Rows, token).ConfigureAwait(false);
                return new(GeometryNotification.Submitted);
            }
            finally { order.Release(); }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return new(GeometryNotification.Failed, error);
        }
    }

    internal ValueTask<TransportSendResult> SubmitPreparedAsync(ReadOnlyMemory<byte> bytes, CancellationToken token,
        SshSendProgress? progress = null, Action? onFirstSubmission = null) => SubmitAsync(bytes, token, progress, onFirstSubmission);

    private async ValueTask<TransportSendResult> SubmitAsync(ReadOnlyMemory<byte> bytes, CancellationToken token,
        SshSendProgress? progress, Action? onFirstSubmission, TelnetProtocol.InputFraming? framing = null,
        byte[]? payload = null, InputCutoff? cutoff = null)
    {
        bool issued = false;
        int definite = 0;
        try
        {
            await writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var connection = ConnectedWire;
                if (connection is SshSessionWire ssh)
                {
                    progress ??= new(bytes.Length);
                    definite = await ssh.WriteChannelAsync(bytes, token, progress, onFirstSubmission, cutoff).ConfigureAwait(false);
                }
                else
                {
                    int end = bytes.Length;
                    bool stopping = false;
                    while (definite < end)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!stopping && cutoff?.IsStopped() == true)
                        {
                            end = WireCutoff(definite, cutoff, framing, payload);
                            stopping = true;
                            if (definite == end) break;
                        }
                        int requested = end - definite;
                        if (IsSerial) requested = SerialSessionWire.WriteRequestLength(requested);
                        if (definite == 0) onFirstSubmission?.Invoke();
                        issued = true;
                        int count = await connection.WriteSomeAsync(bytes.Slice(definite, requested), token).ConfigureAwait(false);
                        if (count < 0 || count > requested)
                            throw new IOException("The terminal write did not make valid progress.");
                        definite += count;
                        issued = false;
                        if (count == 0 && !IsSerial) throw new IOException("The terminal write made no progress.");
                        if (IsSerial) SerialSessionWire.RequireFullWrite(count, requested);
                    }
                }
                if (definite == bytes.Length)
                    return new(TransportSubmission.Submitted, payload?.Length ?? bytes.Length, null, progress?.State);
                int confirmed = framing?.PayloadPrefix(payload, definite) ?? definite;
                if (confirmed > 0 && cutoff?.ClosePrefix is { } close)
                {
                    byte[] suffix = close(confirmed);
                    if (suffix.Length != 0) await connection.WriteAsync(EncodeInput(suffix), token).ConfigureAwait(false);
                }
                return new(confirmed == 0 ? TransportSubmission.NotIssued : TransportSubmission.Partial,
                    confirmed, new OperationCanceledException(cutoff!.Token), progress?.State);
            }
            finally { writer.Release(); }
        }
        catch (Exception error) when (IsSubmissionFailure(error))
        {
            if (progress is not null)
            {
                progress.Settle();
                SshSendState state = progress.State;
                TransportSubmission status = FailedSubmission(state.DefiniteBytes, state.Uncertain is not null);
                return new(status, state.DefiniteBytes, error, state);
            }
            var outcome = FailedSubmission(definite, issued);
            int confirmed = framing?.PayloadPrefix(payload, definite) ?? definite;
            return new(outcome, confirmed, error);
        }
        finally { progress?.Settle(); }
    }

    private static bool IsSubmissionFailure(Exception error) => error is IOException or SocketException or
        OperationCanceledException or ObjectDisposedException or InvalidDataException or System.Security.Cryptography.CryptographicException;

    private static TransportSubmission FailedSubmission(int definiteWireBytes, bool uncertain) => uncertain
        ? TransportSubmission.Uncertain : definiteWireBytes == 0 ? TransportSubmission.NotIssued : TransportSubmission.Partial;

    private static int WireCutoff(int written, InputCutoff cutoff, TelnetProtocol.InputFraming? framing, byte[]? payload)
    {
        if (written == 0) return 0;
        if (framing is not { } mapping) return cutoff.BoundaryAtOrAfter(written);
        int raw = mapping.PayloadPrefix(payload, written);
        if (mapping.WirePrefix(payload, raw) < written) raw++;
        return mapping.WirePrefix(payload, cutoff.BoundaryAtOrAfter(raw));
    }

    internal ValueTask<TransportSendResult> SubmitPayloadAsync(byte[] payload, CancellationToken token,
        Action? onFirstSubmission = null, InputCutoff? cutoff = null)
    {
        if (telnet is null) return SubmitAsync(payload, token, null, onFirstSubmission, cutoff: cutoff);
        TelnetProtocol.InputFraming framing;
        lock (protocolGate) framing = telnet.CaptureInputFraming();
        byte[] framed = framing.Encode(payload, reusable: payload);
        return ReferenceEquals(framed, payload) ? SubmitAsync(payload, token, null, onFirstSubmission, cutoff: cutoff)
            : SubmitAsync(framed, token, null, onFirstSubmission, framing, payload, cutoff);
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var outcome = await SubmitPreparedAsync(bytes, token).ConfigureAwait(false);
        if (outcome.Error is { } error) ExceptionDispatchInfo.Throw(error);
    }

    internal async ValueTask<int> WriteBoundedPayloadAsync(byte[] payload, int[] encodedEnds,
        CancellationToken lifetime, CancellationToken stop, Func<int, byte[]>? codecClose = null,
        Action? onFirstSubmission = null, CancellationToken beforeFirstSubmission = default, bool secret = false,
        Action? onQueued = null, Func<bool>? stopRequested = null)
    {
        using var waiting = beforeFirstSubmission.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime, stop, beforeFirstSubmission)
            : CancellationTokenSource.CreateLinkedTokenSource(lifetime, stop);
        try
        {
            Task acquisition = writer.WaitAsync(waiting.Token);
            if (!acquisition.IsCompleted) onQueued?.Invoke();
            await acquisition.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !lifetime.IsCancellationRequested) { return 0; }
        catch (OperationCanceledException error) when (lifetime.IsCancellationRequested)
        { throw new PasteWriteException(error, 0, TransportSubmission.NotIssued); }
        int definite = 0;
        TelnetProtocol.InputFraming? framing = null;
        SshSendProgress? progress = null;
        bool issued = false;
        byte[] framed = payload;
        try
        {
            int submitted;
            if (wire is SshSessionWire ssh)
            {
                progress = new(payload.Length);
                submitted = await ssh.WriteBoundedPayloadAsync(payload, encodedEnds, lifetime, stop, progress,
                    onFirstSubmission, beforeFirstSubmission, stopRequested)
                    .ConfigureAwait(false);
            }
            else
            {
                int[] wireEnds = encodedEnds;
                if (telnet is not null)
                {
                    wireEnds = encodedEnds.ToArray();
                    lock (protocolGate) framing = telnet.CaptureInputFraming();
                    framed = framing.Value.Encode(payload, wireEnds);
                }
                int position = 0, complete = 0;
                while (position < framed.Length)
                {
                    lifetime.ThrowIfCancellationRequested();
                    bool boundary = complete == 0 ? position == 0 : wireEnds[complete - 1] == position;
                    if (boundary && (stop.IsCancellationRequested || stopRequested?.Invoke() == true)) break;
                    // Once part of a scalar or protocol unit was issued, complete only that unit.
                    int requested = boundary ? framed.Length - position : wireEnds[complete] - position;
                    if (IsSerial) requested = SerialSessionWire.WriteRequestLength(requested);
                    if (position == 0)
                    {
                        beforeFirstSubmission.ThrowIfCancellationRequested();
                        onFirstSubmission?.Invoke();
                    }
                    var connection = ConnectedWire;
                    issued = true;
                    int count = await connection.WriteSomeAsync(framed.AsMemory(position, requested), lifetime).ConfigureAwait(false);
                    if (count < 0 || count > requested)
                        throw new IOException("The terminal write did not make valid progress.");
                    definite = position += count;
                    issued = false;
                    if (count == 0 && !IsSerial) throw new IOException("The terminal write made no progress.");
                    while (complete < wireEnds.Length && wireEnds[complete] <= position) complete++;
                    if (IsSerial) SerialSessionWire.RequireFullWrite(count, requested);
                }
                submitted = complete == 0 ? 0 : encodedEnds[complete - 1];
            }
            if (submitted > 0 && submitted < payload.Length && codecClose is not null)
            {
                byte[] suffix = codecClose(submitted);
                if (suffix.Length != 0)
                {
                    issued = true;
                    await ConnectedWire.WriteAsync(EncodeInput(suffix), lifetime).ConfigureAwait(false);
                }
            }
            return submitted;
        }
        catch (Exception error) when (IsSubmissionFailure(error))
        {
            SshSendState? ssh = progress?.State;
            definite = ssh?.DefiniteBytes ?? definite;
            throw new PasteWriteException(error, framing?.PayloadPrefix(payload, definite) ?? definite,
                FailedSubmission(definite, issued || ssh?.Uncertain is not null));
        }
        finally
        {
            if (secret && !ReferenceEquals(framed, payload))
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(framed);
            writer.Release();
        }
    }

    internal async Task WriteBatchAsync(IEnumerable<byte[]> messages, CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (byte[] message in messages) await ConnectedWire.WriteAsync(message, token).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }

    internal Task DisconnectAsync()
    {
        lock (lifecycle) return close ??= Task.Run(DisconnectCoreAsync);
    }

    private async Task DisconnectCoreAsync()
    {
        try
        {
            await opening.CancelAsync().ConfigureAwait(false);
            if (start is { } startup)
            {
                try { await startup.ConfigureAwait(false); }
                catch (Exception) when (startup.IsFaulted || startup.IsCanceled) { }
            }
            if (wire is SshSessionWire ssh)
            {
                try
                {
                    await ssh.DisconnectForDeliveryAsync().ConfigureAwait(false);
                    while (await ReceiveAsync(CancellationToken.None).ConfigureAwait(false) == ReceiveOutcome.Delivered) { }
                }
                finally
                {
                    await SealDeliveryAsync().ConfigureAwait(false);
                    await ssh.ReleaseReceiveAsync().ConfigureAwait(false);
                }
            }
            else
            {
                var connected = wire;
                Task deviceRelease = Task.Run(async () =>
                {
                    if (connected is not null) await connected.CloseAsync().ConfigureAwait(false);
                });
                await SealDeliveryAsync().ConfigureAwait(false);
                await deviceRelease.ConfigureAwait(false);
            }
        }
        finally
        {
            try { await lateOpenRelease.ConfigureAwait(false); }
            finally
            {
                await writer.WaitAsync().ConfigureAwait(false);
                writer.Release();
                await geometryWriter.WaitAsync().ConfigureAwait(false);
                geometryWriter.Release();
                opening.Dispose();
                actualRelease.TrySetResult();
            }
        }
    }

    private async Task SealDeliveryAsync()
    {
        await reader.WaitAsync().ConfigureAwait(false);
        try
        {
            eofDelivered = true;
            receiveBuffer = [];
            receiveMemory.Dispose();
            deliverySettled.TrySetResult();
        }
        finally { reader.Release(); }
    }
}
