using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal static partial class ClipboardPaste
{
    private const uint UnicodeText = 13;
    private static readonly SemaphoreSlim AnalysisSlots = new(4);

    internal static Task<TerminalPaste?> CaptureAsync(Window window, long maximum, CancellationToken token)
    {
        nint owner = new WindowInteropHelper(window).Handle;
        return CaptureOwnedAsync(() => Read(owner, maximum, token), token);
    }

    internal static Task<TerminalPaste?> CaptureBlockAsync(nint block, long maximum, CancellationToken token) =>
        CaptureOwnedAsync(() => CopyUnicodeBlock(block, maximum, token), token);

    private static async Task<TerminalPaste?> CaptureOwnedAsync(
        Func<(string Text, ResourceReservation Memory)?> read, CancellationToken token)
    {
        await AnalysisSlots.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var captured = read();
                return captured is { } source ? TerminalPaste.CreateOwned(source.Text, source.Memory, token) : null;
            }, token);
        }
        finally { AnalysisSlots.Release(); }
    }

    private static (string Text, ResourceReservation Memory)? Read(nint owner, long maximum, CancellationToken token)
    {
        if (!OpenClipboard(owner))
            throw new IOException(Text("PasteClipboardUnavailable"));
        try
        {
            if (!IsClipboardFormatAvailable(UnicodeText)) return null;
            nint data = GetClipboardData(UnicodeText);
            if (data == 0) throw new IOException(Text("PasteClipboardUnavailable"));
            return CopyUnicodeBlock(data, maximum, token);
        }
        finally { CloseClipboard(); }
    }

    private static unsafe (string Text, ResourceReservation Memory)? CopyUnicodeBlock(nint data, long maximum, CancellationToken token)
    {
        nuint units = GlobalSize(data) / sizeof(char);
        nint locked = GlobalLock(data);
        if (locked == 0) throw new IOException(Text("PasteClipboardUnavailable"));
        try
        {
            int scan = (int)Math.Min((ulong)units, (ulong)Math.Min(maximum / sizeof(char) + 1, int.MaxValue));
            int length = FindTerminator(new ReadOnlySpan<char>((void*)locked, scan), token);
            if (length < 0)
            {
                if ((ulong)units > (ulong)(maximum / sizeof(char)))
                    throw new ArgumentException(Text("PasteSourceTooLarge"));
                throw new IOException(Text("PasteClipboardUnavailable"));
            }
            if (length == 0) return null;
            var memory = TerminalPaste.ReserveSource(length);
            try { return (new string((char*)locked, 0, length), memory); }
            catch (OutOfMemoryException)
            {
                memory.Dispose();
                throw new ResourceLimitException(Text("InputQueueFull"));
            }
            catch { memory.Dispose(); throw; }
        }
        finally { GlobalUnlock(data); }
    }

    private static int FindTerminator(ReadOnlySpan<char> text, CancellationToken token)
    {
        const int page = 32 * 1024;
        for (int offset = 0; offset < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(page, text.Length - offset);
            int index = text.Slice(offset, count).IndexOf('\0');
            if (index >= 0) return offset + index;
            offset += count;
        }
        return -1;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);
    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);
}
