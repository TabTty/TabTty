param(
    [ValidateRange(3, 1800)] [int]$Seconds = 1800,
    [ValidateSet('equal', 'one-busy')] [string]$Distribution = 'equal',
    [string]$OutputDirectory,
    [string]$TestAssembly,
    [string]$Dotnet
)
$ErrorActionPreference='Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$id=[guid]::NewGuid().ToString('N')
$fixtureDir=Join-Path $workspace 'artifacts/interop-ssh'
$fixture=Join-Path $fixtureDir "load-$id.cjs"
$portFile=Join-Path $fixtureDir "load-$id.port"
$cycleStem = Join-Path $workspace 'artifacts/performance/cycle'
$cycle = "$cycleStem.bin"
if (!(Test-Path -LiteralPath $cycle -PathType Leaf)) {
    & (Get-Command node).Source (Join-Path $workspace 'tests/fixtures/generate-workload.cjs') $cycleStem
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $workspace 'artifacts/architecture-workload' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = Join-Path $OutputDirectory "architecture-$Distribution-$Seconds-$id.json"
$serverLog = Join-Path $OutputDirectory "architecture-$Distribution-$Seconds-$id.server.log"
foreach ($path in @($cycle, (Join-Path $fixtureDir 'node_modules/ssh2/package.json'), (Join-Path $fixtureDir 'fixture_host'))) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing workload fixture input $path" }
}
if (!$Dotnet) {
    $bundled = Join-Path $workspace 'artifacts/macro-dotnet-sdk/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet).Source }
}
if (!$TestAssembly) { $TestAssembly = Join-Path $workspace 'tests/TabTty.App.Tests/bin/Release/net10.0-windows/TabTty.App.Tests.dll' }
if (!(Test-Path -LiteralPath $TestAssembly -PathType Leaf)) { throw "Build the Release App tests before running the workload." }
$server = [IO.File]::ReadAllText((Join-Path $workspace 'tests/fixtures/ssh-peer.cjs'))
$server = $server.Replace("`r`n", "`n")
$server=$server.Replace("const algorithms = {};", @'
const algorithms = { cipher: ['aes128-gcm@openssh.com'] };
const cycle = fs.readFileSync(process.env.TABTTY_ARCH_CYCLE);
const producers = [];
let started = false;
let transferRekeyStarted = false;
function feed(channel, rate) {
  if (!rate) return;
  const begin = process.hrtime.bigint();
  let offset = 0, credit = 0, chunk = 0;
  const sizes = [1,7,64,4096];
  function tick() {
    const due = Math.floor(Number(process.hrtime.bigint()-begin) / 1e9 * rate);
    while (credit + sizes[chunk % sizes.length] <= due) {
      const size = sizes[chunk++ % sizes.length];
      const next = offset + size;
      const data = next <= cycle.length ? cycle.subarray(offset, next) :
        Buffer.concat([cycle.subarray(offset), cycle.subarray(0, next - cycle.length)]);
      offset = next % cycle.length;
      credit += size;
      if (!channel.write(data)) { channel.once('drain', tick); return; }
    }
    timer = setTimeout(tick, 10);
  }
  let timer;
  channel.on('close', () => { clearTimeout(timer); channel.removeListener('drain', tick); });
  tick();
}
'@)
$server=$server.Replace("['tabtty-interop', 'tabtty-sftp', 'tabtty-sftp-denied'].includes(ctx.username)", "(['tabtty-interop', 'tabtty-sftp', 'tabtty-sftp-denied'].includes(ctx.username) || /^tabtty-load-(?:[0-9]|1[0-9])$/.test(ctx.username))")
$transferUser = if ($Distribution -eq 'one-busy') { 'tabtty-load-10' } else { 'tabtty-load-18' }
$server=$server.Replace("if (username !== 'tabtty-sftp') return rejectSftp();", "if (username !== '$transferUser') return rejectSftp();")
$server=$server.Replace("        status(id, 0);"+"`n"+"      });"+"`n"+"      channel.on('READ'", @'
        if (!transferRekeyStarted) {
          transferRekeyStarted = true;
          return client.rekey(error => {
            if (error) return channel.destroy(error);
            process.stderr.write('Rekey completed while the SFTP write reply was held.\n');
            setTimeout(() => status(id, 0), 12);
          });
        }
        setTimeout(() => status(id, 0), 12);
      });
      channel.on('READ'
'@)
$server=$server.Replace("        if (username.startsWith('tabtty-sftp')) {", @'
        if (username.startsWith('tabtty-load-')) {
          const index = Number(username.slice('tabtty-load-'.length));
          const rate = process.env.TABTTY_ARCH_DISTRIBUTION === 'one-busy'
            ? index === 10 ? 1048576 : 0 : 1048576 / 20;
          producers[index] = {channel, rate};
          // Commands remain within the synthetic loopback fixture.
          channel.on('data', data => {
            if (!started && index === 19 && data.toString() === 'START') {
              started = true;
              producers.forEach(p => feed(p.channel, p.rate));
            }
          });
          return;
        }
        if (username.startsWith('tabtty-sftp')) {
'@)
[IO.File]::WriteAllText($fixture,$server,[Text.UTF8Encoding]::new($false))
$env:TABTTY_ARCH_FIXTURE=$fixture
$env:TABTTY_ARCH_CYCLE=$cycle
$env:TABTTY_ARCH_DISTRIBUTION=$Distribution
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$p=$null
try {
    $p=Start-Process -FilePath (Get-Command node).Source -ArgumentList @(('"' + $fixture + '"'), ('"' + $portFile + '"')) -WorkingDirectory $fixtureDir -RedirectStandardError $serverLog -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 100 -and !(Test-Path -LiteralPath $portFile); $i++) {
        if ($p.HasExited) { throw 'Workload fixture exited.' }
        Start-Sleep -Milliseconds 50
    }
    $port=Get-Content -LiteralPath $portFile -Raw
    Write-Output "Workload report will be written to $report"
    & $Dotnet $TestAssembly --architecture-workload $port $Seconds $report
    $result=$LASTEXITCODE
    if (!(Test-Path -LiteralPath $report)) {
        if ($result -eq 0) { throw 'The workload did not write a report.' }
        exit $result
    }
    $reportBody = Get-Content -LiteralPath $report -Raw
    $reportBody
    $metrics = $reportBody | ConvertFrom-Json
    if ($result -eq 0 -and $Seconds -eq 1800 -and
        (!$metrics.completed -or $metrics.interactions -lt 600 -or !$metrics.localTargetPassed -or
         !$metrics.stopTargetPassed -or $metrics.rawLogs.Count -ne 20 -or $metrics.disconnects.Count -ne 0 -or
         $metrics.textLogs -ne 20 -or $metrics.terminalLogs -ne 20 -or
         @($metrics.rawLogs | Where-Object { !$_.CycleMatches }).Count -gt 0 -or
         !(Select-String -LiteralPath $serverLog -SimpleMatch 'Rekey completed while the SFTP write reply was held.'))) {
        exit 1
    }
    exit $result
} finally {
    if ($p -and !$p.HasExited) { Stop-Process -Id $p.Id }
    foreach ($generated in @($fixture,$portFile)) {
        if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated }
    }
}
