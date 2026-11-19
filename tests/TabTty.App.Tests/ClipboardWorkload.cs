using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void PrepareClipboardFixtureStation()
    {
        // Each window station has its own clipboard. This mode runs in a
        // dedicated process, which releases the station and desktop on exit.
        const uint windowStationAccess = 0x037f;
        const uint desktopAccess = 0x01ff;
        nint original = GetProcessWindowStation();
        nint station = CreateWindowStation("TabTtyClipboard-" + Guid.NewGuid().ToString("N"),
            0, windowStationAccess, 0);
        if (station == 0 || station == original)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An isolated clipboard station could not be created.");
        if (!SetProcessWindowStation(station) || GetProcessWindowStation() != station)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An isolated clipboard station could not be assigned.");
        nint desktop = CreateDesktop("ClipboardFixture", 0, 0, 0, desktopAccess, 0);
        if (desktop == 0 || !SetThreadDesktop(desktop))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An isolated clipboard desktop could not be assigned.");
    }

    private static async Task CheckClipboardAndSftpAsync(App app, string directory, int port, bool nativeClipboard)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        string path = Path.Combine(directory, "native-clipboard.ttty");
        await ConnectionProfile.CreateSsh("Clipboard", "127.0.0.1", "tabtty-clipboard", port,
            methods: ["password"]).SaveNewAsync(path, token);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        const int pageUnits = 8192;
        const int middle = 1_048_577;
        string original = "😀" + new string('x', pageUnits - 3) + "𠮷" + new string('y', middle) +
            "表\t次\r\n<U+202E>\u202E\u202C終";
        using var block = nativeClipboard ? null : new ClipboardBlock(original.Length);
        var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        var heldCapture = new TaskCompletionSource<TerminalPaste>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holdNextCapture = false;
        var terminal = new TerminalTab(path, interaction, (80, 24), captureClipboard: CaptureAsync);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Connected += _ => connected.TrySetResult();
        host.AddTerminalTab(terminal);
        host.Show();
        MainWindow.FormWindow? prompt = null;
        Task? paste = null, originalCapture = null;
        try
        {
            if (app.MaxPasteTextBytes != TerminalPaste.DefaultMaxSourceBytes)
                throw new InvalidOperationException("Settings without an explicit clipboard maximum chose a different default.");
            if (block is not null) await CheckSizeBoundariesAsync(block, token);
            terminal.Start();
            await connected.Task.WaitAsync(token);
            var session = terminal.InputSession!;
            while (!(await session.GetOutputAsync(token)).Contains("clipboard fixture ready"))
                await Task.Delay(10, token);
            using var transfer = await session.OpenFileTransferAsync(token);

            await SaveMaximumAsync(original.Length * 2L);
            ReplaceSource(original);
            paste = terminal.PasteAsync();
            while ((prompt = host.OwnedWindows.OfType<MainWindow.FormWindow>().FirstOrDefault(window => window.IsVisible)) is null)
            {
                if (paste.IsCompleted) throw new InvalidOperationException("The large clipboard text did not open confirmation.");
                await Task.Delay(10, token);
            }
            var provider = transfer.StatAsync("/fixture-await-first-paste-unit"u8.ToArray(), token);
            TerminalSnapshot retained;
            do
            {
                retained = (TerminalSnapshot)await session.GetOutputAsync(token);
                if (!retained.Contains("clipboard-retained-79")) await Task.Delay(10, token);
            }
            while (!retained.Contains("clipboard-retained-79"));
            if (provider.IsCompleted || retained.Find("clipboard-retained-0", matchCase: true) is not { } retainedFirst ||
                retainedFirst.StartRow >= retained.ScreenStartRow)
                throw new InvalidOperationException("The terminal did not retain received history while clipboard review and SFTP were pending.");
            // Fill the production accounting envelope without allocating gigabytes.
            // The review, original source and history remain live through this probe.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var capacity = ApplicationResources.TerminalState;
            using (var pressure = capacity.Reserve(4L * 1024 * 1024 * 1024 - capacity.Used))
            {
                try
                {
                    using var competingCapture = await CaptureAsync(host, app.MaxPasteTextBytes, token);
                    throw new InvalidOperationException("A competing clipboard capture bypassed the application envelope.");
                }
                catch (ResourceLimitException) { }
                if (provider.IsCompleted || !prompt.IsVisible || terminal.InputSession != session)
                    throw new InvalidOperationException("A capacity refusal affected the live review, terminal or SFTP request.");
                GC.KeepAlive(retained);
            }
            ReplaceSource("replacement after capture");
            await SaveMaximumAsync(511);
            string settingsPath = Path.Combine(directory, "settings.json");
            byte[] beforeInvalidSave = await File.ReadAllBytesAsync(settingsPath, token);
            try
            {
                await SaveMaximumAsync(0);
                throw new InvalidOperationException("A zero clipboard maximum was accepted.");
            }
            catch (ArgumentException) { }
            byte[] afterInvalidSave = await File.ReadAllBytesAsync(settingsPath, token);
            if (app.MaxPasteTextBytes != 511 || !beforeInvalidSave.AsSpan().SequenceEqual(afterInvalidSave))
                throw new InvalidOperationException("A failed limit Save changed the effective or persisted limit.");

            var preview = FindControls<TextBox>(prompt).Single(box => box.IsReadOnly && box.IsUndoEnabled == false);
            var value = (IValueProvider)new TextBoxAutomationPeer(preview).GetPattern(PatternInterface.Value)!;
            if (!value.IsReadOnly || value.Value != original[..(pageUnits - 1)])
                throw new InvalidOperationException("The first accessible preview page split the supplementary character.");
            var next = FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("PasteNextPage")));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!value.Value.StartsWith("𠮷", StringComparison.Ordinal))
                throw new InvalidOperationException("The next accessible preview page lost the supplementary character.");
            for (int page = 2; page <= (original.Length - 1) / pageUnits; page++)
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!value.Value.EndsWith("表<U+0009>次<U+000D><U+000A>" + Environment.NewLine +
                "<U+003C>U+202E><U+202E><U+202C>終", StringComparison.Ordinal))
                throw new InvalidOperationException("The accessible preview lost the distinction between controls and literal markers.");

            var delays = FindControls<TextBox>(prompt).Where(box => !box.IsReadOnly).ToArray();
            delays[0].Text = "10000";
            FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("SendPaste")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (await provider.WaitAsync(token) is not { Size: 10 })
                throw new InvalidOperationException("The live SFTP provider did not observe the first clipboard unit.");
            terminal.StopPaste();
            await paste.WaitAsync(token);
            if (await transfer.StatAsync("/fixture-paste-framing"u8.ToArray(), token) is not { Size: 16 })
                throw new InvalidOperationException("Stopping the captured paste changed its payload or bracket closure.");

            await SaveMaximumAsync(original.Length * 2L, autoClose: false);
            ReplaceSource("old clipboard capture");
            holdNextCapture = true;
            originalCapture = terminal.PasteAsync();
            TerminalPaste oldSource = await heldCapture.Task.WaitAsync(token);
            terminal.StopPaste();
            terminal.Disconnect();
            await terminal.Completion.WaitAsync(token);
            using (oldSource.HoldSource()) { }
            connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.Start();
            await connected.Task.WaitAsync(token);
            var replacement = terminal.InputSession!;
            while (!(await replacement.GetOutputAsync(token)).Contains("clipboard fixture ready"))
                await Task.Delay(10, token);
            using var newProvider = await replacement.OpenFileTransferAsync(token);
            var replacementBytes = newProvider.StatAsync("/fixture-await-first-paste-unit"u8.ToArray(), token);
            ReplaceSource(original);
            paste = terminal.PasteAsync();
            while ((prompt = host.OwnedWindows.OfType<MainWindow.FormWindow>().FirstOrDefault(window => window.IsVisible)) is null)
            {
                if (paste.IsCompleted) throw new InvalidOperationException("A cancelled capture blocked the replacement paste.");
                await Task.Delay(10, token);
            }
            FindControls<TextBox>(prompt).First(box => !box.IsReadOnly).Text = "10000";
            FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("SendPaste")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (await replacementBytes.WaitAsync(token) is not { Size: 10 })
                throw new InvalidOperationException("The replacement did not send its own first unit.");
            releaseCapture.SetResult();
            await originalCapture.WaitAsync(token);
            try
            {
                using var unexpected = oldSource.HoldSource();
                throw new InvalidOperationException("The superseded capture still owned its source after actual return.");
            }
            catch (ObjectDisposedException) { }
            terminal.StopPaste();
            await paste.WaitAsync(TimeSpan.FromSeconds(4), token);
            if (await newProvider.StatAsync("/fixture-paste-framing"u8.ToArray(), token) is not { Size: 16 })
                throw new InvalidOperationException("A late old capture changed the replacement Stop or bracket framing.");
        }
        finally
        {
            releaseCapture.TrySetResult();
            if (prompt?.IsVisible == true) prompt.Close();
            terminal.StopPaste();
            if (originalCapture is not null) await originalCapture;
            if (paste is not null) await paste;
            terminal.Disconnect();
            await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (host.IsVisible) host.Close();
        }

        void ReplaceSource(string source)
        {
            if (block is not null) block.Set(source);
            else Clipboard.SetText(source, TextDataFormat.UnicodeText);
        }

        Task SaveMaximumAsync(long limit, bool? autoClose = null) => app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, autoClose ?? app.AutoCloseDisconnectedTabs,
            app.VaultPath, app.SettingsRevision, maxPasteTextBytes: limit);

        async Task<TerminalPaste?> CaptureAsync(Window owner, long maximum, CancellationToken cancellation)
        {
            TerminalPaste? captured = await (block is null ? ClipboardPaste.CaptureAsync(owner, maximum, cancellation) :
                ClipboardPaste.CaptureBlockAsync(block.Handle, maximum, cancellation));
            if (!holdNextCapture || captured is null) return captured;
            holdNextCapture = false;
            heldCapture.SetResult(captured);
            try
            {
                await releaseCapture.Task.WaitAsync(token);
                return captured;
            }
            catch
            {
                captured.Dispose();
                throw;
            }
        }
    }

    private static async Task CheckSizeBoundariesAsync(ClipboardBlock block, CancellationToken token)
    {
        foreach (string input in new[] { new string('a', 512), new string('語', 512), string.Concat(Enumerable.Repeat("😀", 256)) })
        {
            block.Set(input);
            using TerminalPaste? exact = await ClipboardPaste.CaptureBlockAsync(block.Handle, 1024, token);
            if (exact is not { SourceBytes: 1024, RequiresConfirmation: false } || exact.Text != input)
                throw new InvalidOperationException("The exact UTF-16 threshold was counted or confirmed incorrectly.");
            try
            {
                using var oversized = await ClipboardPaste.CaptureBlockAsync(block.Handle, 1023, token);
                throw new InvalidOperationException("The clipboard truncated the source to an odd-byte limit.");
            }
            catch (ArgumentException) { }
        }
        block.Set(new string('a', 513));
        using TerminalPaste? above = await ClipboardPaste.CaptureBlockAsync(block.Handle, 1026, token);
        if (above is not { SourceBytes: 1026, RequiresConfirmation: true, HasNewline: false })
            throw new InvalidOperationException("The first newline-free source above 1 KiB did not require confirmation.");
        foreach (string input in new[] { "a\r", "a\n", "a\r\n" })
        {
            block.Set(input);
            using TerminalPaste? newline = await ClipboardPaste.CaptureBlockAsync(block.Handle, 32, token);
            if (newline is not { RequiresConfirmation: true, EndsWithNewline: true } || newline.SourceBytes != input.Length * 2)
                throw new InvalidOperationException("The clipboard did not preserve the original CR/LF text and confirmation.");
        }
    }

    private sealed class ClipboardBlock(int units) : IDisposable
    {
        public nint Handle { get; } = GlobalAlloc(2, checked((nuint)(units + 1) * 2));

        internal void Set(string text)
        {
            if (Handle == 0) throw new OutOfMemoryException();
            nint data = GlobalLock(Handle);
            if (data == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                char[] characters = (text + "\0").ToCharArray();
                Marshal.Copy(characters, 0, data, characters.Length);
            }
            finally { GlobalUnlock(Handle); }
        }

        public void Dispose() { if (Handle != 0) GlobalFree(Handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowStationW")]
    private static extern nint CreateWindowStation(string name, uint flags, uint access, nint security);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(nint station);

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDesktopW")]
    private static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);
}
