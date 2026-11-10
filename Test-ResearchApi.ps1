param([string]$BaseUrl='http://localhost:5086')
$ErrorActionPreference='Stop'
function Research($request,$previous=$null,$action='ask',$years=$null) {
    $body=@{request=$request;previousRequest=$previous;action=$action;compareYears=$years}|ConvertTo-Json -Depth 12
    Invoke-RestMethod "$BaseUrl/api/research" -Method Post -ContentType 'application/json' -Body $body
}
$first=Research @{question='What did Brazil say about human rights in 2024?';searchMode='keyword'}
if($first.resolvedRequest.delegation -ne 'Brazil' -or $first.result.evidence.Count -eq 0){throw 'Initial context failed'}
$follow=Research @{question='same question in 2023';delegation='Brazil';year=2024;searchMode='keyword'} $first.resolvedRequest
if($follow.resolvedRequest.year -ne 2023 -or $follow.resolvedRequest.question -match '2024'){throw 'Year follow-up retained stale context'}
$session=Research @{question='previous session';searchMode='keyword'} $first.resolvedRequest
if($session.resolvedRequest.session -ne 78 -or $null -ne $session.resolvedRequest.year){throw 'Previous session was treated as a year'}
$conflict=Research @{question='What did Brazil say about human rights?';delegation='France';searchMode='keyword'}
if($conflict.kind -ne 'clarification' -or $null -ne $conflict.result){throw 'Delegation conflict was not blocked'}
$yearConflict=Research @{question='human rights in 2024';year=2023;searchMode='keyword'}
if($yearConflict.kind -ne 'clarification'){throw 'Year conflict was not blocked'}
$topic=Research @{question='Which delegations discussed universal periodic review?';searchMode='keyword'}
$group=Research @{question='Show group statements';searchMode='keyword'} $topic.resolvedRequest 'groups'
if(-not $group.resolvedRequest.groupsOnly -or @($group.result.evidence|Where-Object {-not $_.representedGroup}).Count){throw 'Group refinement failed'}
$comparison=Research @{question='Compare by year';searchMode='keyword';generate=$true} $topic.resolvedRequest 'compare' @(2023,2024)
if($comparison.comparison.Count -ne 2 -or $comparison.resolvedRequest.generate){throw 'Comparison limits failed'}
foreach($year in $comparison.comparison){if(@($year.result.evidence|Where-Object {$_.date -notlike "$($year.year)*"}).Count){throw 'Comparison crossed year boundaries'}}
$reset=Research @{question='same question in 2023';searchMode='keyword'}
if($reset.kind -ne 'clarification'){throw 'Request without context reused server history'}
Write-Host 'PASS: conversation context, year/session refinement, scope conflict blocking, group refinement, bounded comparison, and independent new conversation.'
