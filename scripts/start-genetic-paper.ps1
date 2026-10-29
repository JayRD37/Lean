[CmdletBinding()]
param(
    [Parameter()]
    [string] $SupervisorConfig
)

$ErrorActionPreference = "Stop"
$previousPSModulePath = [Environment]::GetEnvironmentVariable("PSModulePath", "Process")
$env:PSModulePath = Join-Path $PSHOME "Modules"
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($SupervisorConfig)) {
    $SupervisorConfig = Join-Path $repositoryRoot "examples/genetic-discovery/supervisor.json"
}
$userDotnetRoot = Join-Path $env:USERPROFILE ".dotnet"
$userDotnet = Join-Path $userDotnetRoot "dotnet.exe"
$previousDotnetRoot = [Environment]::GetEnvironmentVariable("DOTNET_ROOT", "Process")
$previousPath = [Environment]::GetEnvironmentVariable("PATH", "Process")

if (Test-Path -LiteralPath $userDotnet) {
    $dotnetExecutable = $userDotnet
    $env:DOTNET_ROOT = $userDotnetRoot
    $env:PATH = "$userDotnetRoot;$env:PATH"
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw "Install the .NET SDK pinned by global.json before starting paper trading."
    }
    $dotnetExecutable = $dotnetCommand.Source
}

$keyVariable = "ALGORY_ALPACA_API_KEY"
$secretVariable = "ALGORY_ALPACA_API_SECRET"
$quantConnectUserVariable = "ALGORY_QUANTCONNECT_USER_ID"
$quantConnectTokenVariable = "ALGORY_QUANTCONNECT_API_TOKEN"
$previousKey = [Environment]::GetEnvironmentVariable($keyVariable, "Process")
$previousSecret = [Environment]::GetEnvironmentVariable($secretVariable, "Process")
$previousQuantConnectUserId = [Environment]::GetEnvironmentVariable($quantConnectUserVariable, "Process")
$previousQuantConnectToken = [Environment]::GetEnvironmentVariable($quantConnectTokenVariable, "Process")
$keySecure = $null
$secretSecure = $null
$quantConnectTokenSecure = $null
$storedCredentials = $null
$keyPointer = [IntPtr]::Zero
$secretPointer = [IntPtr]::Zero
$quantConnectTokenPointer = [IntPtr]::Zero
$exitCode = 1
$credentialFile = Join-Path $env:LOCALAPPDATA "AlgoryLean/alpaca-paper.credentials.json.dpapi"

try {
    if (Test-Path -LiteralPath $credentialFile) {
        try {
            $storedCredentials = Get-Content -LiteralPath $credentialFile -Raw | ConvertFrom-Json
            if ($storedCredentials.endpoint -ne "https://paper-api.alpaca.markets/v2") {
                throw "Stored credentials are not configured for the Alpaca paper endpoint."
            }
            $keySecure = ConvertTo-SecureString -String $storedCredentials.key
            $secretSecure = ConvertTo-SecureString -String $storedCredentials.secret
        }
        catch {
            throw "Could not read the Windows-protected Alpaca paper credentials for this Windows account. Run scripts/check-alpaca-paper.ps1 to enter and verify them again."
        }
    }
    else {
        $keySecure = Read-Host "Alpaca PAPER API key (input hidden)" -AsSecureString
        $secretSecure = Read-Host "Alpaca PAPER API secret (input hidden)" -AsSecureString
    }
    if ($keySecure.Length -eq 0 -or $secretSecure.Length -eq 0) {
        throw "Both paper credentials are required."
    }

    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($quantConnectUserVariable, "Process"))) {
        $quantConnectUserId = Read-Host "QuantConnect user ID (required by the official Alpaca plugin)"
        if ([string]::IsNullOrWhiteSpace($quantConnectUserId)) { throw "A QuantConnect user ID is required to validate the Alpaca integration entitlement." }
        [Environment]::SetEnvironmentVariable($quantConnectUserVariable, $quantConnectUserId, "Process")
    }
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($quantConnectTokenVariable, "Process"))) {
        $quantConnectTokenSecure = Read-Host "QuantConnect API token (input hidden)" -AsSecureString
        if ($quantConnectTokenSecure.Length -eq 0) { throw "A QuantConnect API token is required to validate the Alpaca integration entitlement." }
        $quantConnectTokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($quantConnectTokenSecure)
        [Environment]::SetEnvironmentVariable($quantConnectTokenVariable, [Runtime.InteropServices.Marshal]::PtrToStringBSTR($quantConnectTokenPointer), "Process")
    }

    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($keySecure)
    $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secretSecure)
    [Environment]::SetEnvironmentVariable($keyVariable, [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer), "Process")
    [Environment]::SetEnvironmentVariable($secretVariable, [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer), "Process")

    & $dotnetExecutable run `
        --project (Join-Path $repositoryRoot "StrategyDiscovery.Supervisor/QuantConnect.Genetic.Supervisor.csproj") `
        --configuration Release `
        --no-build `
        -- --config (Resolve-Path -LiteralPath $SupervisorConfig).Path --enable-paper
    $exitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable($keyVariable, $previousKey, "Process")
    [Environment]::SetEnvironmentVariable($secretVariable, $previousSecret, "Process")
    [Environment]::SetEnvironmentVariable($quantConnectUserVariable, $previousQuantConnectUserId, "Process")
    [Environment]::SetEnvironmentVariable($quantConnectTokenVariable, $previousQuantConnectToken, "Process")
    if ($keyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer) }
    if ($secretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer) }
    if ($quantConnectTokenPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($quantConnectTokenPointer) }
    if ($null -ne $keySecure) { $keySecure.Dispose() }
    if ($null -ne $secretSecure) { $secretSecure.Dispose() }
    if ($null -ne $quantConnectTokenSecure) { $quantConnectTokenSecure.Dispose() }
    $storedCredentials = $null
    [Environment]::SetEnvironmentVariable("DOTNET_ROOT", $previousDotnetRoot, "Process")
    [Environment]::SetEnvironmentVariable("PATH", $previousPath, "Process")
    [Environment]::SetEnvironmentVariable("PSModulePath", $previousPSModulePath, "Process")
}

exit $exitCode



