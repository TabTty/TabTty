using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TabTty.App;

internal static partial class Program
{
    private static async Task CheckManagementStatusBarAsync()
    {
        var form = new FormView();
        var search = new TextBox();
        var list = new ListView();
        form.AddField("SearchProfiles", search);
        form.AddFillingDisplay("ProfileGroups", list);
        form.AddStatusBar();
        var bar = form.Children.OfType<Border>().Single();
        var status = (TextBlock)bar.Child;

        var bounds = new Rect(0, 0, 360, 280);
        var initial = Layout();
        if (status.Text.Length != 0 || initial.Bar.Bottom != bounds.Bottom || initial.Bar.Height == 0)
            throw new InvalidOperationException("An empty status bar did not reserve a line at the bottom.");
        using (form.BeginProgress("SavingChanges"))
        {
            if (status.Text.Length != 0) throw new InvalidOperationException("Fast operation showed a status.");
        }
        await Task.Delay(550);
        if (status.Text.Length != 0) throw new InvalidOperationException("Completed operation showed a late status.");

        using (form.BeginProgress("CheckingPreviousSave"))
        {
            if (status.Text.Length != 0) throw new InvalidOperationException("Progress appeared before the delay.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (status.Text.Length == 0) await Task.Delay(20, timeout.Token);
            if (status.Text != App.Text("CheckingPreviousSave") || initial != Layout())
                throw new InvalidOperationException("Progress showed the wrong message or rearranged the form.");
        }
        if (status.Text.Length != 0 || initial != Layout())
            throw new InvalidOperationException("Clearing progress rearranged the form or left stale text.");
        const string multiLine = "Vault unavailable.\nOpen vault management to create it.";
        form.SetStatusMessage(multiLine);
        if (status.Text.Contains('\n') || !Equals(status.ToolTip, multiLine) ||
            AutomationProperties.GetName(status) != multiLine || initial != Layout())
            throw new InvalidOperationException("A multiline status resized the bar or lost its full text.");

        (Rect Search, Rect List, Rect Bar) Layout()
        {
            form.Measure(bounds.Size);
            form.Arrange(bounds);
            form.UpdateLayout();
            return (Position(search), Position(list), Position(bar));
        }

        Rect Position(FrameworkElement element) =>
            element.TransformToAncestor(form).TransformBounds(new Rect(element.RenderSize));
    }
}
