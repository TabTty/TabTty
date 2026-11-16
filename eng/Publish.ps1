param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/TabTty-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new publish directory.' }
$openssl = Join-Path $repository 'artifacts/OpenSSL-MSVC-win-x64-3.5.9'
$helper = Join-Path $repository 'artifacts/native-win-x64'
& dotnet publish (Join-Path $repository 'src/TabTty.App/TabTty.App.csproj') -c Release -r win-x64 --self-contained true `
    -m:1 -p:UseSharedCompilation=false -p:RequireBundledOpenSsl=true -p:RequireBundledTerminalCore=true `
    "-p:OpenSslNativeDirectory=$openssl" "-p:TabTtyNativeDirectory=$helper" -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publishing failed.' }
& dotnet publish (Join-Path $repository 'src/TabTty.McpBridge/TabTty.McpBridge.csproj') -c Release -r win-x64 `
    --self-contained true -m:1 -p:UseSharedCompilation=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained MCP bridge publishing failed.' }
foreach ($required in @('tabtty_terminal.dll', 'terminal-build.json', 'Rust-LICENSE-MIT.txt', 'Rust-LICENSE-APACHE.txt', 'Rust-COPYRIGHT.txt', 'Unicode-Data-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Missing terminal runtime distribution file: $required" }
}
$terminalBuild = Get-Content -LiteralPath (Join-Path $OutputDirectory 'terminal-build.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'tabtty_terminal.dll')).Hash -ne $terminalBuild.librarySha256) {
    throw 'The terminal runtime does not match its build metadata.'
}
if (Test-Path -LiteralPath (Join-Path $openssl 'native-build.json')) {
    Copy-Item -LiteralPath (Join-Path $openssl 'native-build.json') -Destination $OutputDirectory
}
$deps = Get-Content -LiteralPath (Join-Path $OutputDirectory 'TabTty.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath (Join-Path $repository 'src/TabTty.App/obj/project.assets.json') -Raw | ConvertFrom-Json
$runtimePacks = foreach ($pack in $deps.libraries.PSObject.Properties.Name) {
    if ($pack -notmatch '^runtimepack\.(Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/([^/]+)$') {
        continue
    }
    $id = $Matches[1].ToLowerInvariant()
    $version = $Matches[3]
    $package = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
        Join-Path $_ "$id/$version"
    } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $package) { throw "Missing published runtime pack $id $version." }
    if ($id -like 'microsoft.netcore.*') {
        Copy-Item -LiteralPath (Join-Path $package 'LICENSE.TXT') -Destination (Join-Path $OutputDirectory 'dotnet-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $package 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $OutputDirectory 'dotnet-THIRD-PARTY-NOTICES.txt')
    } else {
        Copy-Item -LiteralPath (Join-Path $package 'LICENSE') -Destination (Join-Path $OutputDirectory 'wpf-LICENSE.txt')
    }
    [ordered]@{
        id = $id
        version = $version
        nugetSha512 = (Get-Content -LiteralPath (Join-Path $package "$id.$version.nupkg.sha512") -Raw).Trim()
    }
}
if (@($runtimePacks).Count -ne 2) { throw 'The published .NET/WPF runtime pack list is incomplete.' }
$runtimePacks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dotnet-build.json') -Encoding ascii
foreach ($file in @('README.md', 'README-ja-jp.md', 'eng/Uninstall-TabTty.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $OutputDirectory
}
$archive = $OutputDirectory + '.zip'
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Output $archive
