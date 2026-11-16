using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TabTty.Core;

internal static class NativeDependencies
{
#pragma warning disable CA2255 // Resolve app-local native dependencies before any import.
    [ModuleInitializer]
    internal static void Initialize() => NativeLibrary.SetDllImportResolver(typeof(NativeDependencies).Assembly,
        (name, assembly, _) => name is "libcrypto-3-x64.dll" or "TabTty.Native.dll" or "tabtty_terminal"
            ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,
                name == "tabtty_terminal" ? "tabtty_terminal.dll" : name), assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32)
            : 0);
#pragma warning restore CA2255
}
