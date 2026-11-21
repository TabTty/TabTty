param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/TabTty-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new publish directory.' }
$pin = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'OpenSslSource.props') -Raw)
$pinnedSource = $pin.Project.PropertyGroup
$openssl = Join-Path $repository ('artifacts/OpenSSL-MSVC-win-x64-' + $pinnedSource.OpenSslSourceVersion)
$helper = Join-Path $repository 'artifacts/native-win-x64'
& dotnet publish (Join-Path $repository 'src/TabTty.App/TabTty.App.csproj') -c Release -r win-x64 --self-contained true `
    -m:1 -p:UseSharedCompilation=false -p:RequireBundledOpenSsl=true -p:RequireBundledTerminalCore=true `
    "-p:OpenSslNativeDirectory=$openssl" "-p:TabTtyNativeDirectory=$helper" -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publishing failed.' }
& dotnet publish (Join-Path $repository 'src/TabTty.McpBridge/TabTty.McpBridge.csproj') -c Release -r win-x64 `
    --self-contained true -m:1 -p:UseSharedCompilation=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained MCP bridge publishing failed.' }
. (Join-Path $PSScriptRoot 'TerminalBundle.ps1')
Confirm-TerminalBundle $OutputDirectory Release
Copy-Item -LiteralPath (Join-Path $openssl 'native-build.json') -Destination $OutputDirectory
$nativeBuild = Get-Content -LiteralPath (Join-Path $OutputDirectory 'native-build.json') -Raw | ConvertFrom-Json
if ($nativeBuild.opensslVersion -cne $pinnedSource.OpenSslSourceVersion -or
        $nativeBuild.sourceSha256 -ine $pinnedSource.OpenSslSourceSha256) {
    throw 'The native build record does not match the pinned OpenSSL source.'
}
$cryptoHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $OutputDirectory 'libcrypto-3-x64.dll')).Hash
$helperHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $OutputDirectory 'TabTty.Native.dll')).Hash
if ($cryptoHash -ine $nativeBuild.librarySha256 -or $helperHash -ine $nativeBuild.helperSha256) {
    throw 'The packaged cryptographic libraries do not match the native build record.'
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
Confirm-TerminalBundle $OutputDirectory Release
$archive = $OutputDirectory + '.zip'
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Output $archive
