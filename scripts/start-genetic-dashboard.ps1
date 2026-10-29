$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'StrategyDiscovery.Dashboard\QuantConnect.Genetic.Dashboard.csproj'
$dotnet = if (Test-Path (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) {
    Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
} else {
    'dotnet'
}
$env:LEAN_DISCOVERY_ROOT = $repoRoot
Write-Host "Starting the local LEAN Discovery dashboard at http://127.0.0.1:5178"
Write-Host 'Press Ctrl+C to stop the dashboard.'
& $dotnet run --project $project --no-launch-profile
