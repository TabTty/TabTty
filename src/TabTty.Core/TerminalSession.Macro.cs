using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TabTty.Core;

public sealed partial class TerminalSession
{
    internal bool IsMacroOwned => macro?.IsActive == true;

    internal Task<bool> SendMacroTextAsync(string text, bool appendEnter, MacroRun owner, CancellationToken token) =>
        SendMacroAsync(text.AsMemory(), appendEnter, false, owner, token);

    internal async Task<bool> SendMacroPasswordAsync(VaultSecret password, bool appendEnter, MacroRun owner, CancellationToken token)
    {
        char[] characters;
        try
        {
            characters = new char[StrictUtf8.GetCharCount(password.Bytes.Span)];
            StrictUtf8.GetChars(password.Bytes.Span, characters);
        }
        catch (DecoderFallbackException) { throw new MacroError(MacroErrorCode.Encoding); }
        try { return await SendMacroAsync(characters, appendEnter, true, owner, token).ConfigureAwait(false); }
        finally { Array.Clear(characters); }
    }

    private async Task<bool> SendMacroAsync(ReadOnlyMemory<char> text, bool appendEnter, bool secret,
        MacroRun owner, CancellationToken token)
    {
        if (!ReferenceEquals(macro, owner)) throw new MacroError(MacroErrorCode.OperationUnavailable);
        string ending = !appendEnter ? "" : profile.Enter switch { "cr" => "\r", "lf" => "\n", _ => "\r\n" };
        using var transient = owner.ReserveTransient((text.Length + ending.Length) * 2L + 64);
        char[] characters = new char[text.Length + ending.Length];
        text.Span.CopyTo(characters); ending.AsSpan().CopyTo(characters.AsSpan(text.Length));
        bool mayHaveSent = false;
        try
        {
            using var unit = inputUnits.Reserve(1);
            using var payloadMemory = inputBytes.Reserve(PasteReservationSize);
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
                await state.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (disconnected) throw new IOException("The terminal session has ended.");
                    byte[] chunk = transmitter.Encode(characters.AsSpan(offset, count), profile.TransmitSevenBitKana);
                    byte[]? wireBytes = null;
                    try
                    {
                        wireBytes = EncodeInput(chunk);
                        mayHaveSent = true;
                        await wire.WriteAsync(wireBytes, deadline.Token).ConfigureAwait(false);
                        if (!secret && LocalEcho)
                        {
                            byte[] echo = new byte[StrictUtf8.GetByteCount(characters.AsSpan(offset, count))];
                            try { StrictUtf8.GetBytes(characters.AsSpan(offset, count), echo); Echo(echo); }
                            finally { CryptographicOperations.ZeroMemory(echo); }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(chunk);
                        if (wireBytes is not null && !ReferenceEquals(wireBytes, chunk)) CryptographicOperations.ZeroMemory(wireBytes);
                    }
                }
                finally { state.Release(); }
                offset += count;
            }
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

    internal Task WaitForMacroPeerCloseAsync(CancellationToken token) =>
        macro is null ? Task.CompletedTask : wire is SshSessionWire ssh ? ssh.PeerClosed.WaitAsync(token) :
            Task.Delay(Timeout.Infinite, token);
}
