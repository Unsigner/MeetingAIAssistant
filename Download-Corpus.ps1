param([switch]$Force)
$ErrorActionPreference = 'Stop'
$dataPath = Join-Path $PSScriptRoot 'Data'
$rawPath = Join-Path $dataPath 'raw'
New-Item -ItemType Directory -Force -Path $rawPath | Out-Null
$manifest = Get-Content -Raw -LiteralPath (Join-Path $dataPath 'manifest.json') | ConvertFrom-Json
$receiptPath = Join-Path $dataPath 'downloads.json'
$previous = @()
if (Test-Path -LiteralPath $receiptPath) { $previous = @(Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json) }
$receipts = @()
foreach ($document in $manifest) {
    $destination = Join-Path $rawPath $document.fileName
    $downloaded = $false
    if ($Force -or -not (Test-Path -LiteralPath $destination)) {
        Write-Host "Downloading $($document.symbol) from the UN Digital Library"
        $temporary = $destination + '.download'
        try {
            Invoke-WebRequest -Uri $document.pdfUrl -OutFile $temporary -TimeoutSec 120
            $stream = [System.IO.File]::OpenRead($temporary)
            try { $signature = New-Object byte[] 5; $read = $stream.Read($signature, 0, 5) } finally { $stream.Dispose() }
            if ($read -ne 5 -or [System.Text.Encoding]::ASCII.GetString($signature) -ne '%PDF-') { throw "Source did not return a PDF: $($document.symbol)" }
            Move-Item -LiteralPath $temporary -Destination $destination -Force
            $downloaded = $true
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
    }
    $old = $previous | Where-Object { $_.documentId -eq $document.id } | Select-Object -First 1
    $receipts += [pscustomobject]@{
        documentId = $document.id
        sourceUrl = $document.pdfUrl
        downloadedAtUtc = if ($downloaded) { [DateTime]::UtcNow.ToString('o') } elseif ($old) { $old.downloadedAtUtc } else { (Get-Item -LiteralPath $destination).LastWriteTimeUtc.ToString('o') }
        sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        bytes = (Get-Item -LiteralPath $destination).Length
    }
    Write-Host "Ready: $($document.symbol)"
}
ConvertTo-Json -InputObject $receipts -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Host 'Download manifest saved. To rebuild the index: dotnet run -- --ingest'
