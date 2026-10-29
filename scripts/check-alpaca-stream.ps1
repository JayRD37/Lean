[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$credentialFile = Join-Path $env:LOCALAPPDATA "AlgoryLean/alpaca-paper.credentials.json.dpapi"
$previousKey = [Environment]::GetEnvironmentVariable("ALGORY_ALPACA_API_KEY", "Process")
$previousSecret = [Environment]::GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", "Process")
$previousSmoke = [Environment]::GetEnvironmentVariable("ALGORY_RUN_ALPACA_STREAM_SMOKE", "Process")
$keySecure = $null
$secretSecure = $null
$keyPointer = [IntPtr]::Zero
$secretPointer = [IntPtr]::Zero
$stored = $null

try {
    if (-not (Test-Path -LiteralPath $credentialFile)) {
        throw "No protected Alpaca paper credentials were found. Run scripts/check-alpaca-paper.ps1 first."
    }
    $stored = Get-Content -LiteralPath $credentialFile -Raw | ConvertFrom-Json
    if ($stored.endpoint -ne "https://paper-api.alpaca.markets/v2") {
        throw "Stored credentials are not configured for the Alpaca paper endpoint."
    }
    $keySecure = ConvertTo-SecureString -String $stored.key
    $secretSecure = ConvertTo-SecureString -String $stored.secret
    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($keySecure)
    $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secretSecure)
    [Environment]::SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer), "Process")
    [Environment]::SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer), "Process")
    [Environment]::SetEnvironmentVariable("ALGORY_RUN_ALPACA_STREAM_SMOKE", "1", "Process")

    $dotnet = Join-Path $env:USERPROFILE ".dotnet/dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    & $dotnet test (Join-Path $root "StrategyDiscovery.Tests/QuantConnect.Genetic.Tests.csproj") `
        --configuration Release `
        --filter "FullyQualifiedName~AlpacaDirectPaperStreamSmokeTests" `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Alpaca paper market-data stream authentication failed." }
}
finally {
    [Environment]::SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", $previousKey, "Process")
    [Environment]::SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", $previousSecret, "Process")
    [Environment]::SetEnvironmentVariable("ALGORY_RUN_ALPACA_STREAM_SMOKE", $previousSmoke, "Process")
    if ($keyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer) }
    if ($secretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer) }
    if ($null -ne $keySecure) { $keySecure.Dispose() }
    if ($null -ne $secretSecure) { $secretSecure.Dispose() }
    $stored = $null
}
