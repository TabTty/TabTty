using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckNoticeStackAsync(App app, string directory)
    {
        var tab = new TerminalTab(Path.Combine(directory, "notice.tttym"));
        var window = new MainWindow(app, hasForeground: () => true) { Width = 800, Height = 540 };
        window.AddTerminalTab(tab);
        window.Show();
        string language = app.LanguageChoice, theme = app.ThemeChoice;
        try
        {
            var split = new MenuItem();
            tab.FillPaneCommands(split);
            var sideBySide = (MenuItem)split.Items[0];
            sideBySide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            tab.ShowSearch();
            window.UpdateLayout();
            var panes = FindControls<TerminalSurface>(tab).ToArray();
            Check(panes.Length == 2, "The test requires a visible split terminal.");
            var viewports = panes.Select(pane => (pane.RenderSize, pane.VerticalOffset)).ToArray();
            var stack = tab.Notices;
            using var program = MacroProgram.Parse(Path.Combine(directory, "notice.tttym"), "#tttym 1\nSleep(0);");
            tab.PrepareMacro(program, []);
            tab.Macro!.Pause();
            tab.Macro.Start();
            window.UpdateLayout();
            var originalStop = ActionButton("MacroStop");
            var search = FindControls<TextBox>(tab).Single(box => box.IsVisible);
            search.Focus();
            int acknowledgements = 0;
            void Acknowledge() { acknowledgements++; stack.Remove("report"); }
            stack.ShowHint("hint", "mouse hint", TimeSpan.FromMinutes(1));
            stack.ShowReport("report", "report", Acknowledge);
            window.UpdateLayout();
            Check(Top(originalStop) < Top(Label("mouse hint")) && Top(Label("mouse hint")) < Top(Label("report")),
                "New notices were not appended in order.");
            var passive = Label("mouse hint");
            var hit = tab.TerminalArea.InputHitTest(passive.TranslatePoint(new Point(2, 2), tab.TerminalArea)) as DependencyObject;
            Check(hit is not null && !ReferenceEquals(hit, stack) && !stack.IsAncestorOf(hit),
                "The hint intercepted terminal pointer input.");
            stack.ShowReport("report", "updated report", Acknowledge);
            stack.Remove("hint");
            window.UpdateLayout();
            var ok = ActionButton("OK");
            Check(ReferenceEquals(Keyboard.FocusedElement, search) && Label("updated report").IsVisible &&
                Top(originalStop) < Top(Label("updated report")), "Updating notices disrupted focus or ordering.");
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(acknowledgements == 1, "A stale OK button acknowledged a completed incident again.");

            foreach (var (locale, appearance) in new[] { ("en", "light"), ("ja", "dark") })
            {
                await app.SaveSettingsAsync(app.SettingsRevision, new(Language: locale, Theme: appearance));
                for (int index = 0; index < 12; index++)
                    stack.ShowReport(index, index + " · " + string.Concat(Enumerable.Repeat("保存先に書き込めませんでした。", 5)), () => { });
                stack.ShowHint("explanation", "deferred hint", TimeSpan.FromMilliseconds(280));
                window.UpdateLayout();
                string count = string.Format(App.Text("NoticeStackShow"), 14);
                var show = FindControls<Button>(stack).Single(button => Equals(button.Content, count));
                Check(show.IsVisible && show.Focusable && AutomationProperties.GetName(show) == count &&
                    ActionButton("MacroStop").IsVisible, "Overflow hid the count or direct macro stop action.");
                await Task.Delay(350);
                show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var list = FindControls<ScrollViewer>(stack).Single(view => view.IsVisible);
                Check(list.ScrollableHeight > 0 && FindControls<TextBlock>(list).Any(text => text.Text == "deferred hint"),
                    "A hidden hint expired or the expanded list could not scroll.");
                var messages = FindControls<TextBlock>(list).Where(label => label.Text.Contains(" · ")).ToArray();
                Check(messages.Length == 12 && messages.Select(label => label.Text.Split(' ')[0])
                    .SequenceEqual(Enumerable.Range(0, 12).Select(index => index.ToString())), "Overflow reordered reports.");
                stack.HintsActive = false;
                for (int index = 0; index < 12; index++) stack.Remove(index);
                window.UpdateLayout();
                await Task.Delay(350);
                stack.HintsActive = true;
                window.UpdateLayout();
                Check(Label("deferred hint").IsVisible, "An inactive tab consumed the hint duration.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (FindControls<TextBlock>(stack).Any(text => text.Text == "deferred hint"))
                    await Task.Delay(20, deadline.Token);
            }
            Check(panes.Select(pane => (pane.RenderSize, pane.VerticalOffset)).SequenceEqual(viewports),
                "Notifications changed a terminal viewport or scroll position.");
            ActionButton("MacroStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await tab.Macro.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            tab.UpdateLabels();
            window.UpdateLayout();
            Check(!FindControls<Button>(stack).Any(button => button.IsVisible && Equals(button.Content, App.Text("MacroStop"))) &&
                FindControls<TextBlock>(tab).Any(text => text.IsVisible && text.Text.Contains(App.Text("MacroStateStopped"))),
                "The stopped macro retained an action or lost its inspectable state.");

            Button ActionButton(string key) => FindControls<Button>(stack)
                .Single(button => button.IsVisible && Equals(button.Content, App.Text(key)));
            TextBlock Label(string text) => FindControls<TextBlock>(stack).Single(label => label.Text == text);
            double Top(FrameworkElement element) => element.TranslatePoint(new Point(), stack).Y;
        }
        finally
        {
            tab.Macro?.Stop();
            if (tab.Macro is { } run) await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await app.SaveSettingsAsync(app.SettingsRevision, new(Language: language, Theme: theme));
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
