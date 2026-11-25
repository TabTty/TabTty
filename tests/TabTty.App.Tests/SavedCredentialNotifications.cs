using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckSavedCredentialNotificationsAsync(string directory)
    {
        // Fail before transport startup to obtain two real attempt lifetimes without an SSH server.
        string path = Path.Combine(directory, "credential-notices.ttty");
        await File.WriteAllTextAsync(path, "{}");
        var tab = new TerminalTab(path);
        var window = new Window { Width = 640, Height = 480, ShowActivated = false, Content = tab };
        window.Show();
        try
        {
            await FailAttempt([], "SshCredentialMissing");
            object first = tab.ConnectionGeneration;
            await FailAttempt([new VaultEntryInfo("fixture", "privateKey")], "VaultCredentialKindMismatch");
            if (ReferenceEquals(first, tab.ConnectionGeneration))
                throw new InvalidOperationException("The reconnect did not start a distinct credential attempt.");
            string[] reasons = [App.Text("SshCredentialMissing"), App.Text("VaultCredentialKindMismatch")];
            var messages = FindControls<TextBlock>(tab.Notices).Where(text => text.IsVisible).ToArray();
            if (messages.Length != 2 || !messages.Zip(reasons).All(pair =>
                pair.First.Text.Contains(tab.TabCaption) && pair.First.Text.Contains(pair.Second)))
                throw new InvalidOperationException("The two failed attempts did not retain their distinct credential reports.");
            var status = FindControls<TextBlock>(TerminalStatusBar(tab)).Single(text => text.Text.StartsWith(App.Text("Failed")));
            if (reasons.Any(reason => status.Text.Contains(reason)) || window.OwnedWindows.Count != 0)
                throw new InvalidOperationException("The credential failure reason was repeated outside its floating notice.");
            foreach (var ok in FindControls<Button>(tab.Notices).Where(button => Equals(button.Content, App.Text("OK"))).ToArray())
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (tab.Notices.HasItems || !status.Text.StartsWith(App.Text("Failed")) || tab.IsRunning)
                throw new InvalidOperationException("Acknowledging a credential failure changed the failed connection.");
        }
        finally { window.Close(); }

        async Task FailAttempt(IReadOnlyList<VaultEntryInfo> entries, string reason)
        {
            tab.Start();
            await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                CredentialVault.MatchCredential(entries, "fixture", "password");
                throw new InvalidOperationException("The fixture unexpectedly matched a password.");
            }
            catch (CredentialLookupException error)
            {
                tab.ShowConnectionFailure(error, established: false);
                tab.ShowConnectionFailure(error, established: false);
            }
            window.UpdateLayout();
            if (FindControls<TextBlock>(tab.Notices).Count(label => label.Text.Contains(App.Text(reason))) != 1)
                throw new InvalidOperationException("The same failed lookup produced duplicate or missing notices.");
        }
    }
}
