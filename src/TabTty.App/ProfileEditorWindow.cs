using System.ComponentModel;
using System.IO;
using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class ProfileEditorWindow : Window
{
    private readonly FormView form = new();

    public ProfileEditorWindow(App app, string titleKey, string? identity, ConnectionProfile profile, bool creating,
        bool fromExplorer = false)
    {
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
        UpdatePageTitle(profile.DisplayName);
        app.Profiles.AddEditor(form, profile, titleKey == "EditProfile" ? identity : null, creating, OnSaved, fromExplorer);
        UpdateTitle();
        Closed += (_, _) => { form.Reports.Dispose(); app.EditorClosed(this); };
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
        e.Cancel = !form.CanClose(form.ConfirmDiscardChanges);
    }

    private void OnSaved(string path, string displayName)
    {
        TitleKey = "EditProfile";
        Identity = path;
        UpdateTitle();
        UpdatePageTitle(displayName);
    }

    private void UpdatePageTitle(string displayName)
    {
        if (TitleKey == "EditProfile")
            form.SetPageTitle("EditNamedProfile", PermissionDisplay.Literal(displayName), useTitleBand: true);
        else form.SetPageTitle(TitleKey, useTitleBand: true);
    }

    private void UpdateTitle() => Title = TitleKey switch
    {
        "NewProfile" => Text("NewProfileWindowTitle"),
        "EditProfile" => Text("EditProfileWindowTitle"),
        _ => Identity is null ? Text(TitleKey) : $"{Text(TitleKey)} — {Path.GetFileName(Identity)}",
    };
}
