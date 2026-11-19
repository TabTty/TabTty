using System.ComponentModel;
using System.IO;
using System.Windows;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class VaultEditorWindow : Window
{
    private readonly VaultFileController file;
    private readonly FormView form = new() { IsEnabled = false };
    private bool archived;

    public VaultEditorWindow(VaultFileController file, Action<FormView> build)
    {
        this.file = file;
        Icon = WindowIcons.Vault;
        Width = 940;
        Height = 720;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");
        Content = form;
        form.SetPageTitle("ManageVault");
        build(form);
        UpdateLabels();
    }

    public void EnableManagement() => form.IsEnabled = true;

    public void CloseAfterArchive()
    {
        archived = true;
        Close();
    }

    public void UpdateLabels()
    {
        Title = $"{Text("ManageVault")} — {Path.GetFileName(file.FilePath)}";
        form.UpdateLabels();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (file.IsBusy && !archived && !file.CanDismissMasterChange(this)) e.Cancel = true;
    }
}
