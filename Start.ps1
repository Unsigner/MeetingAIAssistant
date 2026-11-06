$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --no-launch-profile
} finally {
    Pop-Location
}
