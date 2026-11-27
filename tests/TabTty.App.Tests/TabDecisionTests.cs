using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckTabDecisionsAsync(App app, string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        string leftPath = Path.Combine(directory, "Typing.ttty"), rightPath = Path.Combine(directory, "Review.ttty");
        await ConnectionProfile.CreateTelnet("Typing", "127.0.0.1", endpoint.Port).SaveNewAsync(leftPath, token);
        await ConnectionProfile.CreateTelnet("Review", "127.0.0.1", endpoint.Port).SaveNewAsync(rightPath, token);
        var host = new MainWindow(app);
        var movedHost = new MainWindow(app);
        var typing = new TerminalTab(leftPath);
        var target = new TerminalTab(rightPath, null,
            captureClipboard: (_, _, _) => Task.FromResult<TerminalPaste?>(TerminalPaste.Create("a\nb")));
        TerminalTab? macroTab = null;
        try
        {
            host.Show();
            host.AddTerminalTab(typing);
            typing.Start();
            Press(await FindForm("ProfileTrustReview"), "ProfileConnectOnce");
            using var first = await listener.AcceptTcpClientAsync(token);
            await first.GetStream().ReadExactlyAsync(new byte[15], token);
            await Wait(() => typing.InputSession is not null);
            host.UpdateLayout();
            var initial = (TabControl)host.FindName("tabs");
            var split = (Button)initial.Template.FindName("splitGroupsSideButton", initial);
            split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, split));
            host.AddTerminalTab(target);
            host.SelectNotificationTab(typing);
            host.UpdateLayout();
            typing.FocusPane();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var pane = FindControls<TerminalSurface>(typing).Single();
            var focused = Keyboard.FocusedElement;
            target.Start();
            var review = await FindForm("ProfileTrustReview");
            CheckMacroLaunch(target.IsVisible && target.HasPendingDecision && !typing.HasPendingDecision &&
                review.Owner == host && !review.ShowActivated && !review.IsActive && host.IsEnabled &&
                Keyboard.FocusedElement == focused && typing.IsKeyboardFocusWithin &&
                FindControls<DialogText>(review).Any(value => value.Text == target.TabCaption &&
                    System.Windows.Automation.AutomationProperties.GetLabeledBy(value) is TextBlock caption &&
                    caption.Text == string.Format(App.Text("DialogFieldCaption"), App.Text("DecisionTargetTabLabel"))) &&
                FindControls<TextBox>(review).Any(field => field.Text.Contains(endpoint.Port.ToString())),
                "A profile review in the other visible group lost its target or took focus.");
            SendText(pane, "k");
            var firstByte = new byte[1];
            await first.GetStream().ReadExactlyAsync(firstByte, token);
            CheckMacroLaunch(firstByte[0] == 'k', "Typing did not reach the active session.");
            pane.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pane),
                Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await first.GetStream().ReadExactlyAsync(firstByte, token);
            CheckMacroLaunch(firstByte[0] == '\r' && review.IsVisible && target.InputSession is null,
                "Enter in the active session answered the other session's review.");

            movedHost.Show();
            host.SelectNotificationTab(target);
            target.FocusPane();
            var move = FindMenu(host, "WindowMenu", "MoveTab");
            OpenSubmenu(move);
            var otherGroup = (TabControl)movedHost.FindName("tabs");
            var hosts = app.Windows.OfType<MainWindow>().Where(window => window.CanAcceptTabs).ToArray();
            string destination = string.Format(App.Text("WindowGroupName"), Array.IndexOf(hosts, movedHost) + 1,
                System.Windows.Automation.AutomationProperties.GetName(otherGroup));
            Click(move.Items.OfType<MenuItem>().Single(item =>
                System.Windows.Automation.AutomationProperties.GetName(item) == destination));
            CheckMacroLaunch(movedHost.TerminalTabs.Contains(target) && review.Owner == movedHost && target.HasPendingDecision,
                $"The moved question lost its target: tab moved={movedHost.TerminalTabs.Contains(target)}, " +
                $"owner moved={review.Owner == movedHost}, pending={target.HasPendingDecision}.");
            target.ActivatePendingDecision();
            CheckMacroLaunch(app.Windows.OfType<MainWindow.FormWindow>().Single(window => window.IsVisible) == review,
                "Reopening the question created a second request.");
            Press(review, "ProfileConnectOnce");
            using var second = await listener.AcceptTcpClientAsync(token);
            await second.GetStream().ReadExactlyAsync(new byte[15], token);
            await Wait(() => target.InputSession is not null);
            CheckMacroLaunch(!target.HasPendingDecision, "The completed review left its tab marked.");

            host.SelectNotificationTab(typing);
            typing.FocusPane();
            var paste = target.PasteAsync();
            var preview = await FindForm("PastePreview");
            CheckMacroLaunch(!preview.ShowActivated && !preview.IsActive && target.HasPendingDecision &&
                FindControls<TextBox>(preview).Any(box => box.Text.Contains(endpoint.ToString())), "Paste preview lost its frozen session.");
            Press(preview, "Cancel");
            await paste.WaitAsync(token);
            CheckMacroLaunch(second.Available == 0 && !target.HasPendingDecision, "Cancelling a preview submitted input.");

            using var hostKeyBytes = new SshWriter();
            hostKeyBytes.Text("ssh-ed25519");
            hostKeyBytes.String(Convert.FromHexString("D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A"));
            var blob = hostKeyBytes.Span.ToArray();
            var key = new SshHostKey(SshDestination.Parse("test.example", 22), "ssh-ed25519", blob,
                "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(blob)).TrimEnd('='));
            var approve = app.HostTrust.ApproveAsync(movedHost, key, token, target);
            var trust = await FindForm("NewHostKey");
            CheckMacroLaunch(target.HasPendingDecision && !trust.ShowActivated && !trust.IsActive &&
                FindControls<TextBox>(trust).Any(box => box.Text == key.Destination.ToString()) &&
                FindControls<Button>(trust).Single(button => button.IsDefault).IsCancel,
                "Host trust lost its safe default or opened on a different target.");
            Press(trust, "Cancel");
            CheckMacroLaunch(!await approve && !target.HasPendingDecision, "Dismissal granted trust.");

            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var challenge = new SshChallenge(SshDestination.Parse("test.example", 22), "", "", [new("Code", false)]);
            var response = target.RequestSshChallengeAsync(challenge, cancelled.Token);
            var question = await FindForm("KeyboardInteractive");
            var field = FindControls<PasswordBox>(question).Single();
            CheckMacroLaunch(!question.ShowActivated && !question.IsActive && target.HasPendingDecision &&
                !field.IsKeyboardFocused && host.IsEnabled, "A background SSH challenge intercepted another terminal.");
            field.Password = "fixture";
            cancelled.Cancel();
            Press(question, "SendSshAnswers");
            try { await response; throw new InvalidOperationException("A cancelled SSH challenge returned a stale answer."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            using var cleared = field.SecurePassword;
            CheckMacroLaunch(cleared.Length == 0 && !target.HasPendingDecision && !question.IsVisible,
                "A cancelled challenge retained its input or pending indication.");

            var source = Path.Combine(directory, "waiting.tttym");
            macroTab = new TerminalTab(source);
            macroTab.PrepareMacro(MacroProgram.Parse(source,
                "#tttym 1\nvar answer = Dialog(\"Await operator\", \"Continue?\", DialogButtons.YesNo, true, \"\");"), []);
            movedHost.AddTerminalTab(macroTab);
            host.SelectNotificationTab(typing);
            typing.FocusPane();
            var run = macroTab.Macro!;
            run.Start();
            await Wait(() => app.Windows.OfType<MacroDialogWindow>().Any(window => window.IsVisible));
            var macroQuestion = app.Windows.OfType<MacroDialogWindow>().Single();
            CheckMacroLaunch(macroTab.HasPendingDecision && !macroQuestion.ShowActivated && !macroQuestion.IsActive &&
                FindControls<TextBox>(macroQuestion).Single(input => !input.IsReadOnly).IsKeyboardFocused == false &&
                FindControls<Button>(macroQuestion).Single(button => button.IsDefault).Content.Equals(App.Text("No")),
                "A background macro question changed focus or its defined controls.");
            run.Stop();
            await run.Completion.WaitAsync(token);
            CheckMacroLaunch(!macroTab.HasPendingDecision && !macroQuestion.IsVisible,
                "The stopped macro retained an actionable dialog.");
        }
        finally
        {
            typing.Disconnect();
            target.Disconnect();
            macroTab?.Macro?.Stop();
            await Task.WhenAll(typing.Completion, target.Completion, macroTab?.Completion ?? Task.CompletedTask);
            if (!host.IsClosing) host.Close();
            if (!movedHost.IsClosing) movedHost.Close();
        }

        async Task<MainWindow.FormWindow> FindForm(string title)
        {
            MainWindow.FormWindow? form;
            while ((form = app.Windows.OfType<MainWindow.FormWindow>().FirstOrDefault(window =>
                window.IsVisible && window.Title == App.Text(title))) is null) await Task.Delay(10, token);
            return form;
        }
        async Task Wait(Func<bool> ready)
        {
            while (!ready()) await Task.Delay(10, token);
        }
        static void Press(Window window, string key) => FindControls<Button>(window)
            .Single(button => Equals(button.Content, App.Text(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
