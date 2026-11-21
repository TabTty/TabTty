function Get-TerminalBundleFiles {
    $inventory = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'TerminalBundle.props') -Raw)
    $inventory.Project.ItemGroup.TerminalBundleFile | ForEach-Object { $_.Include }
}

function Confirm-TerminalBundle([string]$Directory, [string]$Configuration) {
    foreach ($file in (Get-TerminalBundleFiles)) {
        if (!(Test-Path -LiteralPath (Join-Path $Directory $file) -PathType Leaf)) {
            throw "Missing terminal runtime distribution file: $file"
        }
    }
    $record = Get-Content -LiteralPath (Join-Path $Directory 'terminal-build.json') -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($record.schemaVersion -ne 1 -or $record.target -cne 'x86_64-pc-windows-msvc' -or
        $record.configuration -cne $Configuration -or $record.librarySha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Invalid terminal-build.json for Windows x64 $Configuration."
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Directory 'tabtty_terminal.dll')).Hash
    if ($hash -ne $record.librarySha256) {
        throw 'The terminal runtime does not match its build metadata.'
    }
}
