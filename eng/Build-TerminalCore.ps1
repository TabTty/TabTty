param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
function Get-TerminalSourceHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository "artifacts/terminal-win-x64/$Configuration"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$crate = Join-Path $repository 'src/TabTty.Terminal'
$targetDirectory = Join-Path $repository 'artifacts/terminal-rust-build'
$target = 'x86_64-pc-windows-msvc'
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Install the stable Rust MSVC x64 toolchain and Visual Studio C++ build tools to build the terminal core.'
}
$savedLinker = $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER
$savedLib = $env:LIB
$savedRustFlags = $env:RUSTFLAGS
try {
    # A developer shell already supplies these. Discover them for ordinary dotnet builds.
    if (-not $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER -and -not (Get-Command link.exe -ErrorAction SilentlyContinue)) {
        $visualStudio = Join-Path $env:ProgramFiles 'Microsoft Visual Studio'
        $toolset = Get-ChildItem -Path "$visualStudio/*/*/VC/Tools/MSVC/*" -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/Hostx64/x64/link.exe') } |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $toolset) { throw 'MSVC x64 linker was not found. Run from a Visual Studio x64 developer shell.' }
        $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = Join-Path $toolset.FullName 'bin/Hostx64/x64/link.exe'
        $msvcLib = Join-Path $toolset.FullName 'lib/x64'
        if (-not (Test-Path -LiteralPath (Join-Path $msvcLib 'libcmt.lib'))) {
            $msvcLib = Join-Path $toolset.FullName 'lib/onecore/x64'
        }
        $sdkRoot = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Windows Kits/10/Lib'
        $sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                (Test-Path -LiteralPath (Join-Path $_.FullName 'ucrt/x64/libucrt.lib')) -and
                (Test-Path -LiteralPath (Join-Path $_.FullName 'um/x64/kernel32.lib'))
            } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $sdk -or -not (Test-Path -LiteralPath (Join-Path $msvcLib 'libcmt.lib')) ) {
            throw 'The MSVC static runtime or Windows SDK x64 libraries are missing.'
        }
        $env:LIB = @($msvcLib, (Join-Path $sdk.FullName 'ucrt/x64'), (Join-Path $sdk.FullName 'um/x64'), $savedLib) -join ';'
    }
    # The distribution does not require a separately installed VC runtime.
    $env:RUSTFLAGS = (($savedRustFlags + ' -C target-feature=+crt-static').Trim())
    $arguments = @('build', '--offline', '--locked', '--manifest-path', (Join-Path $crate 'Cargo.toml'),
        '--target', $target, '--target-dir', $targetDirectory)
    if ($Configuration -eq 'Release') { $arguments += '--release' }
    & cargo @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Rust terminal core build failed.' }
    $profile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
    $library = Join-Path $targetDirectory "$target/$profile/tabtty_terminal.dll"
    $sysroot = (& rustc --print sysroot).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to locate the Rust standard library licenses.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Copy-Item -LiteralPath $library -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $crate 'Unicode-Data-LICENSE.txt') -Destination $OutputDirectory
    foreach ($license in @('LICENSE-MIT', 'LICENSE-APACHE', 'COPYRIGHT')) {
        Copy-Item -LiteralPath (Join-Path $sysroot "share/doc/rust/$license") -Destination (Join-Path $OutputDirectory "Rust-$license.txt")
    }
    $sourceFiles = @(Get-Item -LiteralPath (Join-Path $crate 'Cargo.toml'), (Join-Path $crate 'Cargo.lock'))
    $sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $crate 'src') -File -Recurse)
    $sources = @($sourceFiles | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($crate.Length + 1).Replace('\', '/'); sha256 = Get-TerminalSourceHash $_.FullName }
    })
    . (Join-Path $PSScriptRoot 'TerminalBundle.ps1')
    [ordered]@{
        schemaVersion = 1
        abiVersion = Get-TerminalBundleAbiVersion
        target = $target
        configuration = $Configuration
        rustc = (& rustc --version --verbose) -join [Environment]::NewLine
        cargo = (& cargo --version)
        rustFlags = $env:RUSTFLAGS
        librarySha256 = Get-TerminalSourceHash (Join-Path $OutputDirectory 'tabtty_terminal.dll')
        sources = $sources
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'terminal-build.json') -Encoding utf8
    # Cargo and license copies preserve old source timestamps. Mark the completed
    # bundle as this target's output so MSBuild can skip an unchanged native build.
    $completedUtc = [DateTime]::UtcNow
    Confirm-TerminalBundle $OutputDirectory $Configuration
    foreach ($file in (Get-TerminalBundleFiles)) {
        (Get-Item -LiteralPath (Join-Path $OutputDirectory $file)).LastWriteTimeUtc = $completedUtc
    }
    Write-Output (Join-Path $OutputDirectory 'tabtty_terminal.dll')
} finally {
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = $savedLinker
    $env:LIB = $savedLib
    $env:RUSTFLAGS = $savedRustFlags
}
