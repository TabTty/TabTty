using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckPostConnectionEditorAsync(App app, string directory)
    {
        string path = Path.Combine(directory, "Editor.ttty");
        var profile = ConnectionProfile.CreateTelnet("Editor", "device.invalid", 23);
        await profile.SaveNewAsync(path, CancellationToken.None);
        const string corrected = "WritePasswordLine(\"entry on another PC\");";

        await CheckLanguageAsync("en", "light");
        await CheckLanguageAsync("ja", "dark");
        CheckMacroLaunch(!File.Exists(app.VaultPath) && !app.Windows.OfType<MainWindow>().Any(),
            "Editing the macro created a credential vault or opened a terminal window.");

        async Task CheckLanguageAsync(string language, string theme)
        {
            await app.SaveSettingsAsync(app.SettingsRevision, new(Language: language, Theme: theme, AutoClose: false));
            CheckPreviewAccessibility();
            var loaded = await ConnectionProfile.LoadAsync(path, CancellationToken.None);
            var window = new ProfileEditorWindow(app, "EditProfile", loaded.OpenedPath, loaded, creating: false);
            window.Show();
            TextBox? source = null;
            try
            {
                var navigation = FindControls<ListBox>(window).Single(list =>
                    list.Items.OfType<ListBoxItem>().Any(item => Equals(item.Tag, "PostConnectionMacro")));
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "PostConnectionMacro"));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                source = Control<TextBox>("PostConnectionSource");
                var completions = Control<ListBox>("PostConnectionCompletions");
                var signature = Control<TextBlock>("PostConnectionSignature");
                var diagnostic = Control<TextBlock>("PostConnectionDiagnostic");
                var underline = Control<PostConnectionUnderline>("PostConnectionUnderline");
                if (language == "ja")
                    CheckMacroLaunch(source.Text == corrected, "Reopening the profile lost the corrected source.");
                window.Activate();
                source.Focus();

                Edit("Wri");
                await WaitFor(() => completions.IsVisible && completions.Items.Count == 2, "two write operations");
                var labels = completions.Items.Cast<PostConnectionCompletion>().Select(item => item.Label).ToArray();
                CheckMacroLaunch(labels.ToHashSet().SetEquals(["WriteLine", "WritePasswordLine"]),
                    "Completion offered an operation outside the embedded language.");
                Press(Key.Down);
                string selected = ((PostConnectionCompletion)completions.SelectedItem).Label;
                CheckMacroLaunch(selected == labels[1], "Down did not move the completion selection.");
                Press(Key.Tab);
                CheckMacroLaunch(source.Text == selected && source.CaretIndex == selected.Length && source.IsKeyboardFocused,
                    "Tab did not insert the selected name at the caret and retain editor focus.");

                Edit("var promptGlobal = Literal(\"$\");\r\nif (true) { var promptInner = Literal(\"#\"); }\r\nWaitPrompt(prom");
                await WaitFor(() => completions.IsVisible &&
                    completions.Items.Cast<PostConnectionCompletion>().Any(item => item.Label == "promptGlobal"), "an in-scope local");
                CheckMacroLaunch(!completions.Items.Cast<PostConnectionCompletion>().Any(item => item.Label == "promptInner"),
                    "Completion offered a local from a closed block.");
                string draft = source.Text;
                Press(Key.Escape);
                CheckMacroLaunch(!completions.IsVisible && source.Text == draft, "Escape changed the draft or retained completion.");

                Edit("WaitPrompt(");
                await WaitFor(() => ActiveArgument("WaitPrompt", "pattern"), "the first argument in an unfinished call");
                Edit("WaitPrompt(Literal(\"$\"), ");
                await WaitFor(() => ActiveArgument("WaitPrompt", "timeoutMs"), "the second argument after a nested call");
                CheckMacroLaunch(signature.Text.Contains("long timeoutMs") && !signature.Text.Contains("[long timeoutMs]"),
                    "The profile prompt wait did not require an explicit timeout.");
                Edit("WaitPrompt(\"Ready>\", 1000);");
                await WaitFor(() => diagnostic.IsVisible && diagnostic.Text.Contains(
                    MacroDiagnostics.Find("WaitPatternRequired", System.Globalization.CultureInfo.GetCultureInfo(language))!),
                    "the shared WaitPrompt type diagnostic");
                Edit("WaitAnyPrompt(");
                await WaitFor(() => ActiveArgument("WaitAnyPrompt", "[name: pattern, ...]"), "named wait alternatives");
                Edit("WritePasswordLine(");
                await WaitFor(() => ActiveArgument("WritePasswordLine", "string name"), "shared credential argument name");
                Edit("WriteLine(\"ready\", ma");
                await WaitFor(() => completions.IsVisible &&
                    completions.Items.Cast<PostConnectionCompletion>().Any(item => item.Label == "markReceiveBoundary"), "the named option");
                Press(Key.Enter);
                CheckMacroLaunch(source.Text.TrimEnd() == "WriteLine(\"ready\", markReceiveBoundary:",
                    "Enter did not complete the named option in place.");

                const string invalid = "if (false) {\r\n    Connect(\"other.ttty\");\r\n}";
                Edit(invalid);
                string expectedLocation = language == "ja" ? "2 行 5 列" : "Line 2, column 5";
                await WaitFor(() => diagnostic.IsVisible && diagnostic.Text.Contains(expectedLocation) &&
                    diagnostic.Text.Contains(TerminalTab.MacroErrorLabel("PostConnectionSubset")), "a localized dead-branch diagnostic with line and column");
                CheckMacroLaunch(underline.IsVisible && underline.Start == invalid.IndexOf("Connect", StringComparison.Ordinal) &&
                    underline.Length == "Connect".Length, "The editor did not underline the offending token.");
                await WaitFor(() => VisualTreeHelper.GetDrawing(underline) is { Bounds.IsEmpty: false }, "a painted inline underline");
                double before = source.ActualHeight;
                window.Height += 80;
                window.Width -= 90;
                await WaitFor(() => source.ActualHeight > before, "a resized source field");
                var location = diagnostic.TranslatePoint(new Point(0, 0), source);
                CheckMacroLaunch(location.Y >= source.ActualHeight - 1 && underline.IsVisible,
                    "The diagnostic did not remain under the source field after resizing.");
                CheckMacroLaunch(source.Text == invalid && window.HasPendingEdits,
                    "Live validation changed or discarded the invalid draft.");
                var save = FindControls<Button>(window).Single(button => Equals(button.Content, App.Text("Save")));
                if (language == "en")
                {
                    byte[] original = await File.ReadAllBytesAsync(path);
                    _ = window.Dispatcher.BeginInvoke(() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    Window? error = null;
                    await WaitFor(() => (error = app.Windows.OfType<Window>().SingleOrDefault(candidate =>
                        candidate.IsVisible && candidate.Owner == window && candidate.Title == App.Text("Error"))) is not null,
                        "a source error on attempting to save the invalid draft");
                    bool reported = FindControls<TextBlock>(error!).Any(message => message.Text.Contains(expectedLocation));
                    FindControls<Button>(error!).Single(button => Equals(button.Content, App.Text("OK")))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    byte[] afterSave = await File.ReadAllBytesAsync(path);
                    CheckMacroLaunch(reported && source.Text == invalid && original.SequenceEqual(afterSave),
                        "Saving an invalid body lost the draft, rewrote the file, or omitted the source location.");
                    source.Focus();
                }

                // Queue a different erroneous draft, then repair it before editing assistance settles.
                Edit("var WriteLine = 1;");
                await WaitFor(() => diagnostic.IsVisible && diagnostic.Text.Contains("WriteLine") &&
                    diagnostic.Text.Contains(language == "ja" ? "予約名" : "reserved"), "a named reserved-symbol diagnostic");
                CheckMacroLaunch(underline.Start == 4 && underline.Length == "WriteLine".Length,
                    "The reserved-name diagnostic did not mark the name token.");
                long compilations = MacroProgram.PostConnectionCompilations;
                Edit("WriteLine(42);");
                Edit(corrected, corrected.IndexOf('(') + 1);
                await WaitFor(() => ActiveArgument("WritePasswordLine", "") && !diagnostic.IsVisible && !underline.IsVisible,
                    "fresh argument help and removal of the corrected diagnostic");
                if (language == "en")
                {
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitFor(() => !window.HasPendingEdits, "saving the corrected source");
                    CheckMacroLaunch((await ConnectionProfile.LoadAsync(path, CancellationToken.None)).PostConnectionSource == corrected,
                        "Corrected source did not survive saving.");
                    CheckMacroLaunch(MacroProgram.PostConnectionCompilations == compilations + 1,
                        "Save repeated strict validation of an unchanged macro source.");
                }
                Console.WriteLine($"PASS PostConnectionEditor {language}/{theme}");

                T Control<T>(string id) where T : FrameworkElement =>
                    FindControls<T>(window).Single(element => AutomationProperties.GetAutomationId(element) == id);

                void Edit(string value, int? caret = null)
                {
                    source.Text = value;
                    source.CaretIndex = caret ?? value.Length;
                }

                void Press(Key key)
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    source.RaiseEvent(args);
                    CheckMacroLaunch(args.Handled, $"Completion did not handle {key}.");
                }

                bool ActiveArgument(string call, string name) => signature.IsVisible &&
                    signature.Text.Contains(call, StringComparison.Ordinal) && (name.Length == 0 ||
                    signature.Inlines.OfType<Run>().Any(run => run.FontWeight.ToOpenTypeWeight() >= 600 &&
                        run.Text.Contains(name, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                if (window.HasPendingEdits && source is not null) source.Text = loaded.PostConnectionSource;
                if (!window.HasPendingEdits) window.Close();
            }
        }

        static async Task WaitFor(Func<bool> ready, string expectation)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ready())
            {
                if (timeout.IsCancellationRequested)
                    throw new InvalidOperationException("The profile editor did not show " + expectation + ".");
                await Task.Delay(15);
            }
        }
    }

    private static void CheckPreviewAccessibility()
    {
        const string label = "MacroInspectSource";
        string prefix = new('x', ReadOnlyPreview.PageUnits - 1);
        foreach (string boundary in new[] { "😀", "\r\n" })
        {
            var window = new MainWindow.FormWindow(null, App.Text(label));
            var contents = new TextBox { IsReadOnly = true, AcceptsReturn = true };
            ReadOnlyPreview.Add(window, label, contents, prefix + boundary + "tail");
            window.Show();
            try
            {
                Verify(1, 2, prefix);
                Navigate("PasteNextPage");
                Verify(2, 2, boundary + "tail");
                Navigate("PasteNextPage");
                Verify(2, 2, boundary + "tail");
                Navigate("PastePreviousPage");
                Verify(1, 2, prefix);
            }
            finally { window.Close(); }

            void Navigate(string key) => FindControls<Button>(window).Single(button => Equals(button.Content, App.Text(key)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            void Verify(int page, int pages, string source)
            {
                string position = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    App.Text("PastePreviewPage"), page, pages);
                if (new TextBoxAutomationPeer(contents).GetName() != App.Text(label) + " " + position ||
                    contents.Text != PermissionDisplay.PastePage(source))
                    throw new InvalidOperationException("The preview lost its accessible page label or split its boundary.");
            }
        }

        var single = new MainWindow.FormWindow(null, App.Text(label));
        var singleContents = new TextBox { IsReadOnly = true };
        ReadOnlyPreview.Add(single, label, singleContents, "");
        single.Show();
        try
        {
            string position = string.Format(System.Globalization.CultureInfo.CurrentCulture, App.Text("PastePreviewPage"), 1, 1);
            if (new TextBoxAutomationPeer(singleContents).GetName() != App.Text(label) + " " + position ||
                FindControls<Button>(single).Any(button => Equals(button.Content, App.Text("PasteNextPage"))))
                throw new InvalidOperationException("A single page did not expose exactly its current page or offered navigation.");
        }
        finally { single.Close(); }
    }
}
