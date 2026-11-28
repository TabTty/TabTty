using System.Windows;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private void OpenMaintenance() => OpenForm("SettingsMaintenance", form =>
    {
        form.AddCheckBoxGroup("RegisterFileType", () =>
        {
            form.AddAction("AssociationRegister", () => ChangeFileRegistration(form.OwnerWindow, register: true));
            form.AddAction("AssociationUnregister", () => ChangeFileRegistration(form.OwnerWindow, register: false));
        });
        form.AddCheckBoxGroup("ManageProfileTrust", () =>
        {
            form.AddAction("RemoveIndividualTrust", () => Run(() => app.Profiles.ManageTrust(form.OwnerWindow)));
            form.AddAction("RemoveAllTrust", () => app.Profiles.ResetTrust(form.OwnerWindow, Report));
        });
        form.AddCheckBoxGroup("MacroManageTrust", () =>
        {
            form.AddAction("RemoveIndividualTrust", () => ManageMacroTrust(form.OwnerWindow));
            form.AddAction("RemoveAllTrust", () => ResetMacroTrust(form.OwnerWindow));
        });
        form.AddActionField("HostKeys", "ManageHostKeys", () => Run(() => app.HostTrust.Show(form.OwnerWindow)));
        form.AddCheckBoxGroup("ManageApplicationSettings", () =>
        {
            form.AddAction("ArchiveBackup", () => Archive().BackUp());
            form.AddAsyncAction("ArchiveRestore", () => RunAsync(() => Archive().RestoreAsync()));
            form.AddAsyncAction("ArchiveVerify", () => RunAsync(() => Archive().VerifyAsync()));
            form.AddAction("RecreateStore", () => ShowRecreateStores(form.OwnerWindow));
        });

        SettingsArchiveWorkflow Archive() => new(app, form.OwnerWindow);

        void Run(Action action)
        {
            try { action(); }
            catch (Exception error) when (ProfileCatalog.IsFileProblem(error)) { Report(error); }
        }

        async Task RunAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (ProfileCatalog.IsFileProblem(error)) { Report(error); }
        }

        void Report(Exception error)
        {
            app.ReportUnresolvedStore(error);
            app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, error.Message,
                Text("SettingsMaintenance"), icon: MessageBoxImage.Error));
        }
    });

    private void ChangeFileRegistration(Window owner, bool register)
    {
        string title = Text(register ? "AssociationRegister" : "AssociationUnregister");
        try
        {
            bool changed = register ? ProfileFileAssociation.RegisterOpenWith() : ProfileFileAssociation.UnregisterOpenWith();
            app.Modal(() => ThemedMessageBox.Show(owner,
                Text(changed ? (register ? "AssociationRegistered" : "AssociationUnregistered") : "AssociationNoChange"),
                title, icon: MessageBoxImage.Information));
        }
        catch (Exception error) when (ProfileFileAssociation.IsRegistrationFailure(error))
        {
            app.Modal(() => ThemedMessageBox.Show(owner, Text("AssociationPartialFailure") + "\n\n" + error.Message,
                title, icon: MessageBoxImage.Error));
        }
    }

    private void ShowRecreateStores(Window owner)
    {
        var dialog = new FormWindow(owner, Text("RecreateStore")) { Width = 620 };
        var choice = new ComboBox { ItemsSource = app.RecoveryStores(), DisplayMemberPath = nameof(JsonStoreRecovery.Store.Name),
            SelectedIndex = 0 };
        dialog.AddField("RecoveryStore", choice);
        dialog.AddAsyncButtons(async () =>
        {
            if (choice.SelectedItem is not JsonStoreRecovery.Store store) return false;
            byte[]? expected = await Task.Run(() => VerifiedFilePublication.ReadDigest(store.Path, store.MaximumBytes));
            var detail = new DialogField(store.Name, new DialogText(store.Path));
            if (app.Modal(() => ThemedMessageBox.Show(dialog, Text("StoreRecoveryWarning"), Text("RecreateStore"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, acceptKey: "RecreateStore", context: detail))
                != MessageBoxResult.Yes) return false;
            await JsonStoreRecovery.RestoreAsync(app, store, store.Defaults(), expected);
            return true;
        }, acceptKey: "RecreateStore", cancelDefault: true);
        app.Modal(dialog.ShowDialog);
    }
}
