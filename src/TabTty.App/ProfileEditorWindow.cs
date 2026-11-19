using System.ComponentModel;
using System.IO;
using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class ProfileEditorWindow : Window
{
    private readonly App app;
    private readonly FormView form = new();

    public ProfileEditorWindow(App app, string titleKey, string? identity, ConnectionProfile profile, bool creating,
        bool fromExplorer = false)
    {
        this.app = app;
        TitleKey = titleKey;
        Identity = identity;
        Icon = WindowIcons.App;
        Width = 940;
        Height = 720;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");
        Content = form;
        form.SetPageTitle(TitleKey);
        app.Profiles.AddEditor(form, profile, titleKey == "EditProfile" ? identity : null, creating, SavedAs, fromExplorer);
        UpdateTitle();
        Closed += (_, _) => app.EditorClosed(this);
    }

    public string TitleKey { get; private set; }
    public string? Identity { get; private set; }
    internal bool HasPendingEdits => form.HasUnsavedChanges || form.IsBusy || form.UnresolvedPublication is not null;

    public void UpdateLabels()
    {
        UpdateTitle();
        form.UpdateLabels();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        e.Cancel = !form.CanClose(() => app.Modal(() => ThemedMessageBox.Show(this, form.ClosePrompt("DiscardChangesAndCloseWindow"),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes);
    }

    private void SavedAs(string path)
    {
        TitleKey = "EditProfile";
        Identity = path;
        UpdateTitle();
        form.SetPageTitle(TitleKey);
    }

    private void UpdateTitle() => Title = Identity is null ? Text(TitleKey) : $"{Text(TitleKey)} — {Path.GetFileName(Identity)}";
}
