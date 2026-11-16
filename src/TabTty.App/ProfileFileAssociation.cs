using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace TabTty.App;

internal static class ProfileFileAssociation
{
    public static void RegisterOnStartup()
    {
        try { RegisterOpenWith(); }
        catch (Exception exception) when (IsRegistrationFailure(exception)) { }
    }

    public static bool IsRegistrationFailure(Exception exception) =>
        ProfileCatalog.IsFileProblem(exception) || exception is SecurityException;

    public static void RegisterOpenWith()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).Equals("TabTty.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
            throw new InvalidOperationException(App.Text("AssociationNeedsExecutable"));

        string assets = Path.Combine(Path.GetDirectoryName(executable)!, "Assets");
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        bool changed = Register(classes, executable, assets);
        const string capabilities = @"Software\TabTty\Capabilities";
        changed |= SetString(Registry.CurrentUser, @"Software\RegisteredApplications", "TabTty", capabilities);
        changed |= SetString(Registry.CurrentUser, capabilities, "ApplicationName", "TabTty");
        changed |= SetString(Registry.CurrentUser, capabilities, "ApplicationDescription", App.Text("FileAssociationDescription"));
        changed |= SetString(Registry.CurrentUser, $@"{capabilities}\FileAssociations", ".ttty", ProfileId);
        changed |= SetString(Registry.CurrentUser, $@"{capabilities}\FileAssociations", ".tttykey", VaultId);
        changed |= SetString(Registry.CurrentUser, $@"{capabilities}\FileAssociations", ".tttym", "TabTty.Macro.1");
        if (changed)
            SHChangeNotify(0x08000000, 0, 0, 0);
    }

    private const string ProfileId = "TabTty.Profile.1";
    private const string VaultId = "TabTty.Vault.1";

    internal static bool Register(RegistryKey classes, string executable, string assets)
    {
        string command = $"\"{executable}\" \"%1\"";
        string editCommand = $"\"{executable}\" --edit \"%1\"";
        string profileIcon = $"\"{executable}\",0";
        string macroIcon = IconPath(".tttym");
        string vaultIcon = IconPath(".tttykey");
        bool changed = SetDefaultIcon(classes, ".ttty", profileIcon);
        changed |= SetDefaultIcon(classes, ".tttym", macroIcon);
        changed |= SetDefaultIcon(classes, ".tttykey", vaultIcon);
        changed |= SetDefaultIcon(classes, ".tttylog", IconPath(".tttylog"));
        changed |= RegisterHandler(".ttty", ProfileId, App.Text("ProfileFileType"), profileIcon, "Document");
        changed |= RegisterHandler(".tttykey", VaultId, App.Text("VaultFiles"), vaultIcon, "Single");
        changed |= RegisterHandler(".tttym", "TabTty.Macro.1", App.Text("RunMacro"), macroIcon);

        const string application = @"Applications\TabTty.exe";
        changed |= SetString(classes, application, "FriendlyAppName", "TabTty");
        changed |= SetString(classes, $@"{application}\SupportedTypes", ".ttty", "");
        changed |= SetString(classes, $@"{application}\SupportedTypes", ".tttym", "");
        changed |= SetString(classes, $@"{application}\shell\open\command", "", command);
        return changed;

        string IconPath(string extension) => $"\"{Path.Combine(assets, extension[1..] + ".ico")}\"";

        bool RegisterHandler(string extension, string identifier, string description, string icon, string? editorSelection = null)
        {
            bool updated = SetString(classes, identifier, "", description);
            updated |= SetDefaultIcon(classes, identifier, icon);
            updated |= SetString(classes, $@"{identifier}\shell\open\command", "", command);
            if (editorSelection is not null)
            {
                updated |= SetString(classes, $@"{identifier}\shell", "", "open");
                updated |= SetString(classes, $@"{identifier}\shell\open", "MultiSelectModel", editorSelection);
                updated |= SetString(classes, $@"{identifier}\shell\edit", "MultiSelectModel", editorSelection);
                updated |= SetString(classes, $@"{identifier}\shell\edit\command", "", editCommand);
            }
            using var openWith = classes.CreateSubKey($@"{extension}\OpenWithProgids");
            if (openWith.GetValueNames().Contains(identifier, StringComparer.OrdinalIgnoreCase) &&
                openWith.GetValueKind(identifier) == RegistryValueKind.None) return updated;
            openWith.SetValue(identifier, Array.Empty<byte>(), RegistryValueKind.None);
            return true;
        }
    }

    private static bool SetDefaultIcon(RegistryKey classes, string fileType, string icon) =>
        SetString(classes, $@"{fileType}\DefaultIcon", "", icon);

    private static bool SetString(RegistryKey classes, string path, string name, string value)
    {
        using var key = classes.CreateSubKey(path);
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string current &&
            current == value && key.GetValueKind(name) == RegistryValueKind.String) return false;
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
