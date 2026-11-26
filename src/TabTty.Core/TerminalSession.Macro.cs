using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TabTty.Core;

internal sealed class MacroSendDeferredException() : TransportAdmissionRejectedException("Macro send preparation was suspended.");

public sealed partial class TerminalSession
{
    internal bool IsMacroOwned => macro?.IsActive == true;
    private bool HasExternalMacro => macro is { IsPostConnection: false };

    internal Task<bool> SendMacroTextAsync(string text, bool appendEnter, bool markReceiveBoundary, MacroRun owner, CancellationToken token) =>
        SendMacroAsync(text.AsMemory(), appendEnter, false, markReceiveBoundary, owner, token, default);

    internal async Task<bool> SendMacroPasswordAsync(VaultSecret password, bool appendEnter, CancellationToken preparation,
        MacroRun owner, CancellationToken token)
    {
        char[] characters;
        try
        {
            characters = new char[StrictUtf8.GetCharCount(password.Bytes.Span)];
            StrictUtf8.GetChars(password.Bytes.Span, characters);
        }
        catch (DecoderFallbackException) { throw new MacroError(MacroErrorCode.Encoding); }
        try { return await SendMacroAsync(characters, appendEnter, true, false, owner, token, preparation).ConfigureAwait(false); }
        finally { Array.Clear(characters); }
    }

    private async Task<bool> SendMacroAsync(ReadOnlyMemory<char> text, bool appendEnter, bool secret, bool markReceiveBoundary,
        MacroRun owner, CancellationToken token, CancellationToken preparation)
    {
        if (!ReferenceEquals(macro, owner)) throw new MacroError(MacroErrorCode.OperationUnavailable);
        string ending = !appendEnter ? "" : profile.Enter switch { "cr" => "\r", "lf" => "\n", _ => "\r\n" };
        using var transient = owner.ReserveTransient((text.Length + ending.Length) * 2L + 64);
        char[] characters = new char[text.Length + ending.Length];
        text.Span.CopyTo(characters); ending.AsSpan().CopyTo(characters.AsSpan(text.Length));
        bool mayHaveSent = false;
        MacroReceiveCutoff? receiveBoundary = null;
        try
        {
            using var unit = inputUnits.Reserve(1);
            using var payloadMemory = inputBytes.Reserve(1024 * 1024 + 64 * 1024);
            Task preflightWait = state.WaitAsync(preparation.CanBeCanceled ? preparation : token);
            if (!preflightWait.IsCompleted) InputSendQueued?.Invoke();
            await preflightWait.ConfigureAwait(false);
            try
            {
                if (disconnected || pasteActive) throw new MacroError(MacroErrorCode.OperationUnavailable);
                byte[] preflight = transmitter.Encode(characters, profile.TransmitSevenBitKana);
                CryptographicOperations.ZeroMemory(preflight);
            }
            finally { state.Release(); }

            for (int offset = 0; offset < characters.Length;)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(1024, characters.Length - offset);
                if (offset + count < characters.Length && char.IsHighSurrogate(characters[offset + count - 1])) count--;
                if (offset + count < characters.Length && characters[offset + count - 1] == '\r' &&
                    characters[offset + count] == '\n') count--;
                using var deadline = PasteWriteDeadline(CancellationToken.None);
                byte[]? chunk = null;
                try
                {
                    ValueTask<int> submission;
                    TerminalEncoding.PasteBoundaries boundaries;
                    bool localEcho;
                    bool first = offset == 0;
                    CancellationToken admission = first && preparation.CanBeCanceled ? preparation : token;
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, admission);
                    await state.WaitAsync(waiting.Token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (disconnected) throw new IOException("The terminal session has ended.");
                        var source = characters.AsSpan(offset, count);
                        chunk = transmitter.Encode(source, profile.TransmitSevenBitKana);
                        boundaries = transmitter.MapPasteBoundaries(source, chunk, profile.TransmitSevenBitKana);
                        Func<int, byte[]>? closeCodec = transmitter.CanClosePrefix ? bytes =>
                            transmitter.ClosePrefix(characters.AsSpan(offset, boundaries.SourcePrefix(bytes)),
                                chunk.AsSpan(0, bytes), profile.TransmitSevenBitKana) : null;
                        localEcho = !secret && LocalEcho;
                        submission = transport.WriteBoundedPayloadAsync(chunk, boundaries.Bytes, deadline.Token, token,
                            closeCodec, onFirstSubmission: () =>
                        {
                            var cutoff = owner.CommitSend(preparation, markReceiveBoundary, first);
                            if (first) receiveBoundary = cutoff;
                        }, secret: secret, beforeFirstSubmission: admission, onQueued: InputSendQueued);
                    }
                    finally { state.Release(); }

                    PasteWriteException? failure = null;
                    int submitted;
                    try { submitted = await submission.ConfigureAwait(false); }
                    catch (PasteWriteException error) when (error.Submission != TransportSubmission.NotIssued)
                    { failure = error; submitted = error.DefiniteBytes; }
                    mayHaveSent |= submitted > 0 || failure is not null;
                    int echoedUnits = localEcho ? boundaries.SourcePrefix(submitted) : 0;
                    if (echoedUnits != 0)
                    {
                        byte[] echo = StrictUtf8.GetBytes(new string(characters, offset, echoedUnits));
                        await state.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            await EchoSubmittedInputAsync(echo, echo.Length).ConfigureAwait(false);
                        }
                        finally { state.Release(); }
                    }
                    if (failure is not null) throw new MacroTermination("UncertainDelivery");
                    if (submitted != chunk.Length)
                    {
                        admission.ThrowIfCancellationRequested();
                        throw new MacroError(MacroErrorCode.OperationUnavailable);
                    }
                }
                finally
                {
                    if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
                }
                offset += count;
            }
            if (characters.Length == 0)
                receiveBoundary = owner.CommitSend(preparation, markReceiveBoundary, first: true);
            if (receiveBoundary is { } boundary) owner.CommitReceiveBoundary(boundary);
            return true;
        }
        catch (MacroSendDeferredException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && preparation.IsCancellationRequested) { return false; }
        catch (EncoderFallbackException) when (!mayHaveSent) { throw new MacroError(MacroErrorCode.Encoding); }
        catch (ResourceLimitException) when (!mayHaveSent) { throw new MacroError(MacroErrorCode.OperationUnavailable); }
        catch (ArgumentException) when (!mayHaveSent) { throw new MacroError(MacroErrorCode.PayloadLimit); }
        catch (Exception error) when (error is IOException or SocketException or CryptographicException or ArgumentException or InvalidOperationException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            if (mayHaveSent) throw new MacroTermination("UncertainDelivery");
            throw new MacroError(MacroErrorCode.OperationUnavailable);
        }
        finally { Array.Clear(characters); }
    }
}
