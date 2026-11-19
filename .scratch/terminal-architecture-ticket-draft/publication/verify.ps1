$ErrorActionPreference = 'Stop'
$publicationDirectory = $PSScriptRoot
$journal = Get-Content -Raw -LiteralPath (Join-Path $publicationDirectory 'journal.json') | ConvertFrom-Json
$manifest = Get-Content -Raw -LiteralPath (Join-Path (Split-Path -Parent $publicationDirectory) 'manifest.json') | ConvertFrom-Json
if ($journal.repository -ne 'TabTty/TabTty' -or $journal.entries.Count -ne 24) { throw 'Unexpected publication journal.' }
$problems = [System.Collections.Generic.List[string]]::new()
$observed = [System.Collections.Generic.List[object]]::new()
$allIssuesText = & gh issue list --repo TabTty/TabTty --state all --limit 100 --json number,title,body,labels,assignees,state,url
if ($LASTEXITCODE -ne 0) { throw 'Issue readback failed.' }
$allIssues = $allIssuesText | ConvertFrom-Json
$map = @($journal.entries | Where-Object key -eq 'map')[0]
$childrenText = & gh api "repos/TabTty/TabTty/issues/$($map.number)/sub_issues?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Native parent readback failed.' }
$childNumbers = @(($childrenText | ConvertFrom-Json) | ForEach-Object number | Sort-Object)
$expectedChildren = @($journal.entries | Where-Object key -ne 'map' | ForEach-Object number | Sort-Object)
if (($childNumbers -join ',') -ne ($expectedChildren -join ',')) { $problems.Add('Native sub-issue set differs from the approved plan.') }
$edgeCount = 0
foreach ($entry in $journal.entries) {
    $matches = @($allIssues | Where-Object number -eq $entry.number)
    if ($matches.Count -ne 1) { $problems.Add("Issue missing from readback: $($entry.number)"); continue }
    $issue = $matches[0]
    if ($issue.title -cne $entry.title) { $problems.Add("Title mismatch: $($entry.number)") }
    if ($issue.state -ne 'OPEN') { $problems.Add("Issue is not open: $($entry.number)") }
    if ($issue.assignees.Count -ne 0) { $problems.Add("Unexpected assignment: $($entry.number)") }
    $expectedBody = (Get-Content -Raw -LiteralPath (Join-Path $publicationDirectory $entry.bodyFile)).Replace("`r`n", "`n").Trim()
    if ($issue.body.Replace("`r`n", "`n").Trim() -cne $expectedBody) { $problems.Add("Body mismatch: $($entry.number)") }
    $actualLabels = @($issue.labels | ForEach-Object name | Sort-Object)
    $expectedLabels = @($entry.labels | Sort-Object)
    if (($actualLabels -join ',') -cne ($expectedLabels -join ',')) { $problems.Add("Label mismatch: $($entry.number)") }
    $blockedText = & gh api "repos/TabTty/TabTty/issues/$($entry.number)/dependencies/blocked_by?per_page=100"
    if ($LASTEXITCODE -ne 0) { throw "Native dependency readback failed: $($entry.number)" }
    $actualBlockers = @(($blockedText | ConvertFrom-Json) | ForEach-Object number | Sort-Object)
    $expectedBlockers = @($entry.blockedByNumbers | Sort-Object)
    if (($actualBlockers -join ',') -ne ($expectedBlockers -join ',')) { $problems.Add("Dependency mismatch: $($entry.number)") }
    $edgeCount += $actualBlockers.Count
    $observed.Add([ordered]@{ key = $entry.key; number = $issue.number; url = $issue.url; title = $issue.title; labels = $actualLabels; blockedBy = $actualBlockers; body = $issue.body })
}
$report = [ordered]@{
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    repository = $journal.repository
    map = $map.url
    issueCount = $observed.Count
    subIssueCount = $childNumbers.Count
    dependencyCount = $edgeCount
    success = ($problems.Count -eq 0)
    problems = $problems.ToArray()
    issues = $observed.ToArray()
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $publicationDirectory 'verification.json') -Encoding utf8
if ($problems.Count -gt 0) { $problems | Write-Output; throw 'Published issue verification failed.' }
Write-Output "Verified $($observed.Count) open, unassigned issues, $($childNumbers.Count) native sub-issues, and $edgeCount native dependency edges; all titles, bodies and labels match."
