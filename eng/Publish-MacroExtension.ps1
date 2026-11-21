param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/macro-extension-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
$extension = Join-Path $OutputDirectory 'extension'
$server = Join-Path $extension 'server'
New-Item -ItemType Directory -Path $extension -Force | Out-Null
Copy-Item -Path (Join-Path $repository 'vscode/tabtty-macro/*') -Destination $extension -Recurse
& dotnet publish (Join-Path $repository 'src/TabTty.MacroEditor/TabTty.MacroEditor.csproj') -c Release -r win-x64 --self-contained true -p:SkipTerminalCoreBuild=true -p:NuGetAudit=false -o $server
if ($LASTEXITCODE -ne 0) { throw 'Macro analysis server publish failed.' }
$sdk = Split-Path (Get-Command dotnet).Source -Parent
Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE.txt') -Destination (Join-Path $server 'dotnet-LICENSE.txt')
$assets = Get-Content -LiteralPath (Join-Path $repository 'src/TabTty.MacroEditor/obj/project.assets.json') -Raw | ConvertFrom-Json
$deps = Get-Content -LiteralPath (Join-Path $server 'TabTty.MacroEditor.deps.json') -Raw | ConvertFrom-Json
$runtime = $deps.libraries.PSObject.Properties.Name | Where-Object {
    $_ -match '^runtimepack\.Microsoft\.NETCore\.App\.Runtime\.win-x64/'
} | Select-Object -First 1
if (-not $runtime) { throw 'The published .NET runtime pack is missing.' }
$version = $runtime.Split('/')[1]
$notices = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    Join-Path $_ "microsoft.netcore.app.runtime.win-x64/$version/THIRD-PARTY-NOTICES.TXT"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $notices) { throw 'The published .NET runtime notices are missing.' }
Copy-Item -LiteralPath $notices -Destination (Join-Path $server 'dotnet-THIRD-PARTY-NOTICES.txt')
$extensionVersion = (Get-Content -LiteralPath (Join-Path $extension 'package.json') -Raw | ConvertFrom-Json).version
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="tabtty-macro" Version="$extensionVersion" Publisher="tabtty" TargetPlatform="win32-x64" />
    <DisplayName>TabTty Macro</DisplayName>
    <Description xml:space="preserve">Editing support for TabTty macros and shared constants.</Description>
    <Categories>Programming Languages</Categories>
  </Metadata>
  <Installation><InstallationTarget Id="Microsoft.VisualStudio.Code" /></Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Details" Path="extension/README.md" Addressable="true" />
  </Assets>
</PackageManifest>
"@
$types = @'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="vsixmanifest" ContentType="text/xml" />
  <Default Extension="json" ContentType="application/json" />
  <Default Extension="js" ContentType="application/javascript" />
  <Default Extension="md" ContentType="text/markdown" />
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="exe" ContentType="application/octet-stream" />
  <Default Extension="txt" ContentType="text/plain" />
  <Default Extension="pdb" ContentType="application/octet-stream" />
</Types>
'@
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'extension.vsixmanifest'), $manifest, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $OutputDirectory '[Content_Types].xml'), $types, [Text.UTF8Encoding]::new($false))
$archive = $OutputDirectory + '.vsix'
$zip = $OutputDirectory + '.zip'
Compress-Archive -LiteralPath $extension, (Join-Path $OutputDirectory 'extension.vsixmanifest'), (Join-Path $OutputDirectory '[Content_Types].xml') -DestinationPath $zip
Move-Item -LiteralPath $zip -Destination $archive
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Output $archive
