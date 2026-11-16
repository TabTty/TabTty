param([string]$PerlExecutable)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repository 'artifacts'
$version = '3.5.9'
$sourceHash = '603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a'
if (-not $PerlExecutable) {
    $portable = Join-Path $artifacts 'build-tools/strawberry/perl/bin/perl.exe'
    if (Test-Path -LiteralPath $portable) { $PerlExecutable = $portable }
    else { $PerlExecutable = (Get-Command perl.exe -ErrorAction Stop).Source }
}
$PerlExecutable = (Resolve-Path -LiteralPath $PerlExecutable).Path
$env:LC_ALL = 'C'
$env:LC_CTYPE = 'C'
$env:LANG = 'C'
if ((& $PerlExecutable -e 'print $^O;') -cne 'MSWin32') {
    throw 'A native Windows Perl, as required by OpenSSL NOTES-WINDOWS.md, is required.'
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$archive = Join-Path $artifacts "openssl-$version.tar.gz"
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest "https://github.com/openssl/openssl/releases/download/openssl-$version/openssl-$version.tar.gz" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $sourceHash) {
    throw 'The OpenSSL source archive does not match the pinned official SHA-256.'
}
$buildRoot = Join-Path $artifacts ('openssl-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
& tar.exe -xzf $archive -C $buildRoot
if ($LASTEXITCODE -ne 0) { throw 'OpenSSL source extraction failed.' }
$source = Join-Path $buildRoot "openssl-$version"
$opensslOutput = Join-Path $artifacts "OpenSSL-MSVC-win-x64-$version"
$helperOutput = Join-Path $artifacts 'native-win-x64'
& cmd.exe /d /c (Join-Path $repository 'src/TabTty.Native/build-openssl.cmd') $source $PerlExecutable $opensslOutput
if ($LASTEXITCODE -ne 0) { throw 'OpenSSL build failed.' }
& cmd.exe /d /c (Join-Path $repository 'src/TabTty.Native/build.cmd') $helperOutput
if ($LASTEXITCODE -ne 0) { throw 'The scoped bcrypt helper build failed.' }
$metadata = [ordered]@{
    opensslVersion = $version
    sourceUrl = "https://github.com/openssl/openssl/releases/download/openssl-$version/openssl-$version.tar.gz"
    sourceSha256 = $sourceHash
    target = 'VC-WIN64A-HYBRIDCRT'
    configuration = 'shared no-asm no-tests no-apps no-docs no-legacy no-module no-autoload-config no-makedepend no-engine'
    librarySha256 = (Get-FileHash (Join-Path $opensslOutput 'libcrypto-3-x64.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    helperSha256 = (Get-FileHash (Join-Path $helperOutput 'TabTty.Native.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    helperSources = @(Get-ChildItem (Join-Path $repository 'src/TabTty.Native/openbsd') -File | ForEach-Object {
        @{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $opensslOutput 'native-build.json') -Encoding utf8NoBOM
Write-Output $opensslOutput
