using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TabTty.Core;

public sealed partial class TerminalSession
{
    internal bool IsMacroOwned => macro?.IsActive == true;
    private bool HasExternalMacro => macro is { IsPostConnection: false };

    internal Task<bool> SendMacroTextAsync(string text, bool appendEnter, bool markReceiveBoundary, MacroRun owner, CancellationToken token) =>
        SendMacroAsync(text.AsMemory(), appendEnter, false, markReceiveBoundary, owner, token);

    internal async Task<bool> SendMacroPasswordAsync(VaultSecret password, bool appendEnter, MacroRun owner, CancellationToken token)
    {
        char[] characters;
        try
        {
            characters = new char[StrictUtf8.GetCharCount(password.Bytes.Span)];
            StrictUtf8.GetChars(password.Bytes.Span, characters);
        }
        catch (DecoderFallbackException) { throw new MacroError(MacroErrorCode.Encoding); }
        try { return await SendMacroAsync(characters, appendEnter, true, false, owner, token).ConfigureAwait(false); }
        finally { Array.Clear(characters); }
    }

    private async Task<bool> SendMacroAsync(ReadOnlyMemory<char> text, bool appendEnter, bool secret, bool markReceiveBoundary,
        MacroRun owner, CancellationToken token)
    {
        if (!ReferenceEquals(macro, owner)) throw new MacroError(MacroErrorCode.OperationUnavailable);
        string ending = !appendEnter ? "" : profile.Enter switch { "cr" => "\r", "lf" => "\n", _ => "\r\n" };
        using var transient = owner.ReserveTransient((text.Length + ending.Length) * 2L + 64);
        char[] characters = new char[text.Length + ending.Length];
        text.Span.CopyTo(characters); ending.AsSpan().CopyTo(characters.AsSpan(text.Length));
        bool mayHaveSent = false;
        MacroReceiveCutoff? receiveBoundary = null;
        Action? captureBoundary = markReceiveBoundary ? () => receiveBoundary = owner.CaptureReceiveBoundary() : null;
        try
        {
            using var unit = inputUnits.Reserve(1);
            using var payloadMemory = inputBytes.Reserve(1024 * 1024 + 64 * 1024);
            await state.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (disconnected || pasteActive) throw new MacroError(MacroErrorCode.OperationUnavailable);
                byte[] preflight = transmitter.Encode(characters, profile.TransmitSevenBitKana);
                CryptographicOperations.ZeroMemory(preflight);
                if (!owner.TryCommitSend()) return false;
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
                byte[]? chunk = null, wireBytes = null;
                try
                {
                    ValueTask<TransportSendResult> submission;
                    bool localEcho;
                    await state.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (disconnected) throw new IOException("The terminal session has ended.");
                        chunk = transmitter.Encode(characters.AsSpan(offset, count), profile.TransmitSevenBitKana);
                        wireBytes = EncodeInput(chunk);
                        localEcho = !secret && LocalEcho;
                        submission = transport.SubmitPreparedAsync(wireBytes, deadline.Token,
                            onFirstSubmission: offset == 0 ? captureBoundary : null);
                    }
                    finally { state.Release(); }

                    var outcome = await submission.ConfigureAwait(false);
                    mayHaveSent |= outcome.Status != TransportSubmission.NotIssued;
                    if (localEcho && (outcome.Status == TransportSubmission.Submitted || outcome.DefiniteBytes > 0))
                    {
                        string portion = new(characters, offset, count);
                        byte[] echo = StrictUtf8.GetBytes(portion);
                        await state.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            int confirmed = ConfirmedInputEchoLength(outcome, chunk, echo, portion, altPrefix: false);
                            await EchoSubmittedInputAsync(echo, confirmed).ConfigureAwait(false);
                        }
                        finally { state.Release(); }
                    }
                    if (outcome.Status != TransportSubmission.Submitted)
                    {
                        if (mayHaveSent) throw new MacroTermination("UncertainDelivery");
                        token.ThrowIfCancellationRequested();
                        throw new MacroError(MacroErrorCode.OperationUnavailable);
                    }
                }
                finally
                {
                    if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
                    if (wireBytes is not null && !ReferenceEquals(wireBytes, chunk)) CryptographicOperations.ZeroMemory(wireBytes);
                }
                offset += count;
            }
            if (receiveBoundary is { } boundary) owner.CommitReceiveBoundary(boundary);
            return true;
        }
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
