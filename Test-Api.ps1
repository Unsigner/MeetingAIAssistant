param([string]$BaseUrl = 'http://localhost:5086')
$ErrorActionPreference = 'Stop'
$corpus = Invoke-RestMethod "$BaseUrl/api/corpus"
if ($corpus.documents.Count -ne 10 -or @($corpus.documents | Where-Object { $_.ingestion.status -ne 'indexed' }).Count) { throw 'Expected ten indexed source documents.' }
$request = @{ question = 'Which delegations discussed universal periodic review?'; year = 2024; searchMode = 'keyword'; generate = $false } | ConvertTo-Json
$answer = Invoke-RestMethod "$BaseUrl/api/ask" -Method Post -Body $request -ContentType 'application/json'
if ($answer.evidence.Count -lt 3) { throw 'Expected several human-rights evidence passages.' }
if ($answer.evidence | Where-Object { $_.date -notlike '2024*' }) { throw 'Year filter failed.' }
$csv = Invoke-WebRequest "$BaseUrl/api/export" -Method Post -Body $request -ContentType 'application/json'
if ($csv.Headers['Content-Type'] -notlike 'text/csv*') { throw 'CSV export failed.' }
$pdf = Invoke-WebRequest "$BaseUrl/api/documents/A_79_PV.19/pdf" -Headers @{ Range = 'bytes=0-4' }
if ($pdf.StatusCode -ne 206) { throw 'PDF range request failed.' }
$election = Invoke-RestMethod "$BaseUrl/api/ask" -Method Post -Body '{"question":"A/79/PV.19","includeProcedural":true,"searchMode":"keyword"}' -ContentType 'application/json'
if ($election.evidence.Count -eq 0 -or @($election.evidence | Where-Object symbol -ne 'A/79/PV.19').Count) { throw 'Election record browsing failed.' }
$context = Invoke-RestMethod "$BaseUrl/api/passages/$($answer.evidence[0].id)"
if (@($context.context | Where-Object interventionId -ne $context.intervention.id).Count) { throw 'Source context crossed speakers.' }
$negative = Invoke-RestMethod "$BaseUrl/api/ask" -Method Post -Body '{"question":"Which states were never elected to the Human Rights Council?"}' -ContentType 'application/json'
if ($negative.evidence.Count -ne 0) { throw 'Unsupported historical negative did not abstain.' }
Write-Host "PASS: ten sources, HRC search, year filter, CSV, PDF range streaming, election browsing, passage context and historical negative handling."
