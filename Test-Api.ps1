param([string]$BaseUrl = 'http://localhost:5086')
$ErrorActionPreference = 'Stop'
$corpus = Invoke-RestMethod "$BaseUrl/api/corpus"
if ($corpus.documents.Count -ne 4) { throw 'Expected four source documents.' }
$request = @{ question = 'Which delegations mentioned plastic pollution?'; year = 2023; generate = $false } | ConvertTo-Json
$answer = Invoke-RestMethod "$BaseUrl/api/ask" -Method Post -Body $request -ContentType 'application/json'
if ($answer.evidence.Count -lt 3) { throw 'Expected several evidence passages.' }
if ($answer.evidence | Where-Object { $_.date -notlike '2023*' }) { throw 'Year filter failed.' }
$csv = Invoke-WebRequest "$BaseUrl/api/export" -Method Post -Body $request -ContentType 'application/json'
if ($csv.Headers['Content-Type'] -notlike 'text/csv*') { throw 'CSV export failed.' }
$pdf = Invoke-WebRequest "$BaseUrl/api/documents/A_78_PV.43/pdf" -Headers @{ Range = 'bytes=0-4' }
if ($pdf.StatusCode -ne 206) { throw 'PDF range request failed.' }
$negative = Invoke-RestMethod "$BaseUrl/api/ask" -Method Post -Body '{"question":"Which States were never elected?"}' -ContentType 'application/json'
if ($negative.evidence.Count -ne 0) { throw 'Unsupported historical question did not abstain.' }
Write-Host "PASS: API corpus, search, year filter, CSV export, PDF streaming and unsupported-question handling. $($answer.evidence.Count) matching excerpts for 2023."
