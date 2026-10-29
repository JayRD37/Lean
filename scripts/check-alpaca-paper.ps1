[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$previousPSModulePath = [Environment]::GetEnvironmentVariable("PSModulePath", "Process")
$env:PSModulePath = Join-Path $PSHOME "Modules"
$keySecure = $null
$secretSecure = $null
$keyPointer = [IntPtr]::Zero
$secretPointer = [IntPtr]::Zero
$storedCredentials = $null
$credentialFile = Join-Path $env:LOCALAPPDATA "AlgoryLean/alpaca-paper.credentials.json.dpapi"

try {
    if (Test-Path -LiteralPath $credentialFile) {
        $storedCredentials = Get-Content -LiteralPath $credentialFile -Raw | ConvertFrom-Json
        if ($storedCredentials.endpoint -ne "https://paper-api.alpaca.markets/v2") {
            throw "Stored credentials are not configured for the Alpaca paper endpoint."
        }
        $keySecure = ConvertTo-SecureString -String $storedCredentials.key
        $secretSecure = ConvertTo-SecureString -String $storedCredentials.secret
    }
    else {
        $keySecure = Read-Host "Alpaca PAPER API key (input hidden)" -AsSecureString
        $secretSecure = Read-Host "Alpaca PAPER API secret (input hidden)" -AsSecureString
    }
    if ($keySecure.Length -eq 0 -or $secretSecure.Length -eq 0) {
        throw "Both paper credentials are required."
    }

    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($keySecure)
    $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secretSecure)
    $headers = @{
        "APCA-API-KEY-ID" = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
        "APCA-API-SECRET-KEY" = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
        "Accept" = "application/json"
    }

    try {
        $account = Invoke-RestMethod -Method Get -Uri "https://paper-api.alpaca.markets/v2/account" -Headers $headers
    }
    catch {
        $statusCode = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        if ($statusCode -eq 401 -or $statusCode -eq 403) {
            throw "Alpaca rejected these credentials (HTTP $statusCode). Confirm they are from the Paper Trading account and try again."
        }
        if ($statusCode) {
            throw "Paper account check failed with HTTP $statusCode. No order was submitted."
        }
        throw "Paper account check failed before an HTTP response was received. No order was submitted."
    }

    if ([string]$account.status -ne "ACTIVE") {
        throw "Paper API credentials are valid, but account status is '$($account.status)'. No order was submitted."
    }

    [PSCustomObject]@{
        Endpoint = "https://paper-api.alpaca.markets"
        AccountStatus = [string]$account.status
        TradingBlocked = [bool]$account.trading_blocked
        AccountBlocked = [bool]$account.account_blocked
        OrdersSubmitted = 0
    } | Format-List
}
finally {
    if ($keyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer) }
    if ($secretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer) }
    if ($null -ne $keySecure) { $keySecure.Dispose() }
    if ($null -ne $secretSecure) { $secretSecure.Dispose() }
    $storedCredentials = $null
    if ($null -ne $headers) { $headers.Clear() }
    [Environment]::SetEnvironmentVariable("PSModulePath", $previousPSModulePath, "Process")
}


