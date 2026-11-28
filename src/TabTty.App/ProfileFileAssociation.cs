using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace TabTty.App;

internal static class ProfileFileAssociation
{
    public static bool IsRegistrationFailure(Exception exception) =>
        ProfileCatalog.IsFileProblem(exception) || exception is SecurityException;

    public static bool RegisterOpenWith()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).Equals("TabTty.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
            throw new InvalidOperationException(App.Text("AssociationNeedsExecutable"));

        string assets = Path.Combine(Path.GetDirectoryName(executable)!, "Assets");
        return NotifyChanges(() =>
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            bool changed = Register(classes, executable, assets);
            const string capabilities = @"Software\TabTty\Capabilities";
            changed |= SetString(Registry.CurrentUser, @"Software\RegisteredApplications", "TabTty", capabilities);
            changed |= SetString(Registry.CurrentUser, capabilities, "ApplicationName", "TabTty");
            changed |= SetString(Registry.CurrentUser, capabilities, "ApplicationDescription", App.Text("FileAssociationDescription"));
            foreach (var (extension, handler) in Handlers)
                changed |= SetString(Registry.CurrentUser, $@"{capabilities}\FileAssociations", extension, handler);
            return changed;
        });
    }

    private const string ProfileId = "TabTty.Profile.1";
    private const string VaultId = "TabTty.Vault.1";
    private const string MacroId = "TabTty.Macro.1";
    private const string ApplicationKey = @"Applications\TabTty.exe";
    private static readonly (string Extension, string Handler)[] Handlers =
        [(".ttty", ProfileId), (".tttykey", VaultId), (".tttym", MacroId)];

    public static bool UnregisterOpenWith() => NotifyChanges(() =>
    {
        using var software = Registry.CurrentUser.OpenSubKey("Software", writable: true);
        return software is not null && Unregister(software, Environment.ProcessPath);
    });

    internal static bool Unregister(RegistryKey software, string? executable)
    {
        const string capabilities = @"TabTty\Capabilities";
        string[] applicationKeys = [.. Handlers.Select(item => item.Handler), ApplicationKey];
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (executable is not null && Path.GetDirectoryName(executable) is { } folder) folders.Add(folder);
        using var classes = software.OpenSubKey("Classes", writable: true);
        bool changed = false;
        if (classes is not null)
        {
            foreach (string name in applicationKeys)
            {
                using var key = classes.OpenSubKey($@"{name}\shell\open\command");
                if (key?.GetValue("") is string command && command.StartsWith('"') && command.EndsWith("\" \"%1\"", StringComparison.Ordinal) &&
                    command.Length > 7 && Path.GetDirectoryName(command[1..^6]) is { } registeredFolder &&
                    Path.GetFileName(command[1..^6]).Equals("TabTty.exe", StringComparison.OrdinalIgnoreCase))
                    folders.Add(registeredFolder);
            }
            foreach (string extension in new[] { ".ttty", ".tttykey", ".tttym", ".tttylog" })
            {
                using var icon = classes.OpenSubKey($@"{extension}\DefaultIcon", writable: true);
                if (icon?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value &&
                    folders.Any(directory => value.Equals(extension == ".ttty"
                        ? $"\"{Path.Combine(directory, "TabTty.exe")}\",0"
                        : $"\"{Path.Combine(directory, "Assets", extension[1..] + ".ico")}\"", StringComparison.OrdinalIgnoreCase)))
                {
                    icon.DeleteValue("");
                    changed = true;
                }
            }
            foreach (var (extension, handler) in Handlers)
            {
                using var openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids", writable: true);
                if (openWith?.GetValueNames().Contains(handler, StringComparer.OrdinalIgnoreCase) != true) continue;
                openWith.DeleteValue(handler);
                changed = true;
            }
            foreach (string name in applicationKeys) changed |= RemoveOwnedKey(classes, name);
        }
        using var registered = software.OpenSubKey("RegisteredApplications", writable: true);
        if (registered?.GetValue("TabTty") is string registration &&
            registration.Equals(@"Software\" + capabilities, StringComparison.OrdinalIgnoreCase))
        {
            registered.DeleteValue("TabTty");
            changed = true;
        }
        return changed | RemoveOwnedKey(software, capabilities);
    }

    private static bool RemoveOwnedKey(RegistryKey parent, string name)
    {
        var key = parent.OpenSubKey(name);
        if (key is null) return false;
        key.Dispose();
        parent.DeleteSubKeyTree(name);
        return true;
    }

    private static bool NotifyChanges(Func<bool> update)
    {
        bool notify = true;
        try { return notify = update(); }
        finally { if (notify) SHChangeNotify(0x08000000, 0, 0, 0); }
    }

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
        changed |= RegisterHandler(".tttym", MacroId, App.Text("RunMacro"), macroIcon);

        const string application = ApplicationKey;
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
