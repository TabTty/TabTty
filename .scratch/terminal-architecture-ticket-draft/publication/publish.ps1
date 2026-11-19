param(
    [switch]$CreateMap,
    [int]$Start = 1,
    [int]$End = 23
)

$ErrorActionPreference = 'Stop'
$publicationDirectory = $PSScriptRoot
$manifestPath = Join-Path (Split-Path -Parent $publicationDirectory) 'manifest.json'
$journalPath = Join-Path $publicationDirectory 'journal.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$journal = Get-Content -Raw -LiteralPath $journalPath | ConvertFrom-Json
if ($manifest.targetRepository -ne 'TabTty/TabTty' -or $journal.repository -ne 'TabTty/TabTty') {
    throw 'Unexpected publication repository.'
}
$repoText = & gh repo view --json nameWithOwner
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the active repository.' }
if (($repoText | ConvertFrom-Json).nameWithOwner -ne 'TabTty/TabTty') {
    throw 'The active repository is not TabTty/TabTty.'
}

function Publish-One {
    param([string]$Key, [string]$Title, [string]$File, [string[]]$Labels, [string[]]$Blockers)
    $existing = @($journal.entries | Where-Object key -eq $Key)
    if ($existing.Count -gt 0) {
        if ($existing.Count -ne 1 -or $existing[0].status -ne 'created') {
            throw "Existing journal entry requires verification: $Key"
        }
        Write-Output "$Key already recorded: $($existing[0].url)"
        return
    }

    $bodyPath = Join-Path $publicationDirectory $File
    if (-not (Test-Path -LiteralPath $bodyPath)) { throw "Missing prepared body: $File" }
    $createArgs = @('issue', 'create', '--repo', 'TabTty/TabTty', '--title', $Title, '--body-file', $bodyPath)
    foreach ($label in $Labels) { $createArgs += @('--label', $label) }
    $parentNumber = $null
    $blockerNumbers = @()
    if ($Key -ne 'map') {
        $parentEntry = @($journal.entries | Where-Object key -eq 'map')
        if ($parentEntry.Count -ne 1 -or $parentEntry[0].status -ne 'created') { throw 'No verified map creation in the journal.' }
        $parentNumber = $parentEntry[0].number
        $createArgs += @('--parent', [string]$parentNumber)
        foreach ($blocker in $Blockers) {
            $blockerEntry = @($journal.entries | Where-Object key -eq $blocker)
            if ($blockerEntry.Count -ne 1 -or $blockerEntry[0].status -ne 'created') { throw "Missing blocker: $blocker" }
            $blockerNumbers += $blockerEntry[0].number
        }
        if ($blockerNumbers.Count -gt 0) { $createArgs += @('--blocked-by', ($blockerNumbers -join ',')) }
    }

    $createOutput = & gh @createArgs 2>&1
    $createExitCode = $LASTEXITCODE
    $outputText = ($createOutput | ForEach-Object { $_.ToString() }) -join "`n"
    $createdUrl = [regex]::Match($outputText, '(?m)^https://github\.com/TabTty/TabTty/issues/(\d+)\s*$')
    if (-not $createdUrl.Success) {
        Write-Output $outputText
        throw "No creation URL returned for $Key; inspect GitHub before retrying. Exit code: $createExitCode"
    }
    $entry = [ordered]@{
        key = $Key
        number = [int]$createdUrl.Groups[1].Value
        url = $createdUrl.Value.Trim()
        title = $Title
        bodyFile = $File
        parentNumber = $parentNumber
        blockedByNumbers = $blockerNumbers
        labels = $Labels
        status = $(if ($createExitCode -eq 0) { 'created' } else { 'created-needs-verification' })
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $journal.entries = @($journal.entries) + $entry
    $journal | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $journalPath -Encoding utf8
    Write-Output "$Key $($entry.url) parent=$parentNumber blockers=$($blockerNumbers -join ',')"
    if ($createExitCode -ne 0) {
        Write-Output $outputText
        throw "Creation of $Key returned an error after creating the issue; verify recorded issue before continuing."
    }
}

if ($CreateMap) {
    Publish-One -Key 'map' -Title 'Complete Rust terminal processing and unify terminal closure' -File 'map.md' -Labels $manifest.mapLabels -Blockers @()
} else {
    if ($Start -lt 1 -or $End -gt $manifest.tickets.Count -or $Start -gt $End) { throw 'Invalid ticket range.' }
    for ($ticketIndex = $Start - 1; $ticketIndex -lt $End; $ticketIndex++) {
        $ticket = $manifest.tickets[$ticketIndex]
        Publish-One -Key $ticket.key -Title $ticket.title -File $ticket.file -Labels $manifest.ticketLabels -Blockers $ticket.blockedBy
    }
}
