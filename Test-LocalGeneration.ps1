param(
    [string]$Question = 'What did Samoa say about financing?',
    [int]$Year = 2023,
    [string]$BaseUrl = 'http://localhost:5086'
)
$ErrorActionPreference = 'Stop'
$request = @{ question = $Question; year = $Year; generate = $true } | ConvertTo-Json
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$answer = Invoke-RestMethod "$BaseUrl/api/ask" -Method Post -Body $request -ContentType 'application/json' -TimeoutSec 330
$timer.Stop()
if ($answer.mode -notlike 'Local RAG*') { throw "Generation did not succeed: $($answer.generationWarning)" }
$ids = @($answer.evidence | ForEach-Object { $_.id })
$citations = [regex]::Matches($answer.answer, '\[([^\]]+)\]')
if ($citations.Count -eq 0) { throw 'The model answer has no evidence citations.' }
foreach ($citation in $citations) {
    if ($citation.Groups[1].Value -notin $ids) { throw 'The model used an invalid evidence identifier.' }
}
[pscustomobject]@{
    Mode = $answer.mode
    Seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1)
    EvidenceCount = $answer.returnedEvidence
    Answer = $answer.answer
    ReviewNote = $answer.generationWarning
} | ConvertTo-Json -Depth 4
