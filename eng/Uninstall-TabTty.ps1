[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$software = (Get-Item -LiteralPath 'HKCU:\').OpenSubKey('Software', -not $WhatIfPreference)
$changed = $false

function Get-RegistryValue($path, $name) {
    $key = $software.OpenSubKey($path)
    if ($null -ne $key) {
        try { return $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
        finally { $key.Dispose() }
    }
}

function Remove-RegistryValue($path, $name) {
    $valueLabel = if ($name) { $name } else { '(Default)' }
    if ($PSCmdlet.ShouldProcess("HKCU\Software\$path [$valueLabel]", 'Remove TabTty registry value')) {
        $key = $software.OpenSubKey($path, $true)
        if ($null -ne $key) {
            try { $key.DeleteValue($name, $false) }
            finally { $key.Dispose() }
            $script:changed = $true
        }
    }
}

function Remove-RegistryKey($path) {
    $key = $software.OpenSubKey($path)
    if ($null -eq $key) { return }
    $key.Dispose()
    if ($PSCmdlet.ShouldProcess("HKCU\Software\$path", 'Remove TabTty registry key')) {
        $software.DeleteSubKeyTree($path, $false)
        $script:changed = $true
    }
}

try {
    # Keep these names in sync with ProfileFileAssociation.cs.
    $handlers = [ordered]@{
        '.ttty' = 'TabTty.Profile.1'
        '.tttykey' = 'TabTty.Vault.1'
        '.tttym' = 'TabTty.Macro.1'
    }
    $applicationKeys = @($handlers.Values) + 'Applications\TabTty.exe'
    $capabilities = 'TabTty\Capabilities'

    # A shared extension's icon may have been changed by another app. Match the
    # icons TabTty writes using the ZIP folder and any previously registered paths.
    $folders = @($PSScriptRoot)
    foreach ($appKey in $applicationKeys) {
        $command = Get-RegistryValue "Classes\$appKey\shell\open\command" ''
        if ($command -is [string] -and $command -match '^"([^"]+\\TabTty\.exe)" "%1"$') {
            $folders += [IO.Path]::GetDirectoryName($Matches[1])
        }
    }
    foreach ($extension in '.ttty', '.tttykey', '.tttym', '.tttylog') {
        $path = "Classes\$extension\DefaultIcon"
        $icon = Get-RegistryValue $path ''
        if ($icon -isnot [string]) { continue }
        foreach ($folder in $folders) {
            $tabTtyIcon = if ($extension -eq '.ttty') {
                '"{0}",0' -f [IO.Path]::Combine($folder, 'TabTty.exe')
            } else {
                '"{0}"' -f [IO.Path]::Combine($folder, 'Assets', ($extension.Substring(1) + '.ico'))
            }
            if ($icon -eq $tabTtyIcon) {
                Remove-RegistryValue $path ''
                break
            }
        }
    }

    foreach ($extension in $handlers.Keys) {
        $path = "Classes\$extension\OpenWithProgids"
        # REG_NONE is read as a byte array (possibly empty). Check the value name
        # directly so a present but empty value is still removed.
        $key = $software.OpenSubKey($path)
        if ($null -ne $key) {
            try { $registered = $key.GetValueNames() -contains $handlers[$extension] }
            finally { $key.Dispose() }
            if ($registered) { Remove-RegistryValue $path $handlers[$extension] }
        }
    }
    if ((Get-RegistryValue 'RegisteredApplications' 'TabTty') -eq "Software\$capabilities") {
        Remove-RegistryValue 'RegisteredApplications' 'TabTty'
    }
    foreach ($appKey in $applicationKeys) { Remove-RegistryKey "Classes\$appKey" }
    Remove-RegistryKey $capabilities
} finally {
    $software.Dispose()
    if ($changed) {
        if (-not ('TabTty.UninstallShell' -as [type])) {
            Add-Type -Namespace TabTty -Name UninstallShell -MemberDefinition @'
[System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
[System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
public static extern void SHChangeNotify(uint eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
'@
        }
        [TabTty.UninstallShell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

if (-not $WhatIfPreference) {
    Write-Output 'TabTty registration cleanup complete. Application and user files were kept.'
}
