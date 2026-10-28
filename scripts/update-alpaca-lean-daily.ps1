[CmdletBinding()]
param(
    [Parameter()]
    [string[]] $Symbols = @("SPY", "QQQ", "IWM"),
    [Parameter()]
    [datetime] $StartDate = [datetime]::new(2018, 1, 1),
    [Parameter()]
    [datetime] $EndDateExclusive = [datetime]::new(2026, 1, 1)
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$credentialFile = Join-Path $env:LOCALAPPDATA "AlgoryLean/alpaca-paper.credentials.json.dpapi"
if (-not (Test-Path -LiteralPath $credentialFile)) {
    throw "Protected Alpaca paper credentials were not found. Run scripts/check-alpaca-paper.ps1 first."
}

$storedCredentials = $null
$keySecure = $null
$secretSecure = $null
$keyPointer = [IntPtr]::Zero
$secretPointer = [IntPtr]::Zero
$headers = $null
$backups = @{}
$temporaryFiles = @()

try {
    $storedCredentials = Get-Content -LiteralPath $credentialFile -Raw | ConvertFrom-Json
    if ($storedCredentials.endpoint -ne "https://paper-api.alpaca.markets/v2") {
        throw "Stored credentials are not configured for the Alpaca paper endpoint."
    }
    $keySecure = ConvertTo-SecureString -String $storedCredentials.key
    $secretSecure = ConvertTo-SecureString -String $storedCredentials.secret
    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($keySecure)
    $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secretSecure)
    $headers = @{
        "APCA-API-KEY-ID" = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
        "APCA-API-SECRET-KEY" = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
        "Accept" = "application/json"
    }

    $easternTime = [TimeZoneInfo]::FindSystemTimeZoneById("Eastern Standard Time")
    $staged = @{}
    foreach ($symbol in ($Symbols | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)) {
        if ($symbol -notmatch '^[A-Z][A-Z0-9.]{0,9}$') {
            throw "Invalid stock symbol '$symbol'."
        }

        $start = $StartDate.ToString("yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
        $end = $EndDateExclusive.ToString("yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
        $uri = "https://data.alpaca.markets/v2/stocks/bars?symbols=$symbol&timeframe=1Day&start=$start&end=$end&limit=10000&adjustment=raw&feed=iex&sort=asc"
        $bars = [Collections.Generic.List[object]]::new()
        $pageCount = 0
        do {
            $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
            foreach ($bar in $response.bars.$symbol) { $bars.Add($bar) }
            $pageCount++
            if ($pageCount -gt 10) { throw "Unexpected pagination volume while downloading $symbol." }
            if ($response.next_page_token) {
                $token = [Uri]::EscapeDataString([string]$response.next_page_token)
                $uri = "https://data.alpaca.markets/v2/stocks/bars?symbols=$symbol&timeframe=1Day&start=$start&end=$end&limit=10000&adjustment=raw&feed=iex&sort=asc&page_token=$token"
            }
        } while ($response.next_page_token)

        if ($bars.Count -lt 1200) { throw "Alpaca returned only $($bars.Count) daily bars for $symbol; expected substantial 2020-2025 coverage." }
        $mergedRows = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $seenDates = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($bar in $bars) {
            $timestamp = [DateTimeOffset]::Parse([string]$bar.t, [Globalization.CultureInfo]::InvariantCulture)
            $date = [TimeZoneInfo]::ConvertTime($timestamp, $easternTime).Date
            if ($date -lt $StartDate.Date -or $date -ge $EndDateExclusive.Date) { continue }
            $dateKey = $date.ToString("yyyyMMdd", [Globalization.CultureInfo]::InvariantCulture)
            if (-not $seenDates.Add($dateKey)) { throw "Duplicate daily bar for $symbol on $dateKey." }
            foreach ($field in @("o", "h", "l", "c", "v")) {
                if ($null -eq $bar.$field) { throw "Alpaca returned an incomplete daily bar for $symbol on $dateKey." }
            }
            $scaled = foreach ($field in @("o", "h", "l", "c")) {
                ([decimal]$bar.$field * [decimal]10000).ToString("0", [Globalization.CultureInfo]::InvariantCulture)
            }
            $volume = ([decimal]$bar.v).ToString("0", [Globalization.CultureInfo]::InvariantCulture)
            $leanFields = @("$dateKey 00:00") + $scaled + @($volume)
            $mergedRows[$dateKey] = [PSCustomObject]@{ Date = $date; Line = $leanFields -join ","; Source = "Alpaca IEX" }
        }
        if ($bars.Count -lt 1200 -or ([TimeZoneInfo]::ConvertTime([DateTimeOffset]::Parse([string]$bars[-1].t), $easternTime).Date -lt [datetime]::new(2025, 12, 30))) {
            throw "Downloaded history for $symbol does not reach the end of the configured period."
        }

        $archivePath = Join-Path $repositoryRoot "Data/equity/usa/daily/$($symbol.ToLowerInvariant()).zip"
        if (-not (Test-Path -LiteralPath $archivePath)) { throw "LEAN daily data archive is missing for $symbol." }
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entry = $archive.GetEntry("$($symbol.ToLowerInvariant()).csv")
            if ($null -eq $entry) { throw "LEAN daily data entry is missing for $symbol." }
            $reader = [IO.StreamReader]::new($entry.Open())
            try {
                while (($line = $reader.ReadLine()) -ne $null) {
                    if ($line.Length -lt 8) { throw "Malformed existing LEAN row for $symbol." }
                    $existingDate = [datetime]::ParseExact($line.Substring(0, 8), "yyyyMMdd", [Globalization.CultureInfo]::InvariantCulture)
                    $existingDateKey = $existingDate.ToString("yyyyMMdd", [Globalization.CultureInfo]::InvariantCulture)
                    if ($existingDate -lt $StartDate.Date -or $existingDate -ge $EndDateExclusive.Date -or -not $mergedRows.ContainsKey($existingDateKey)) {
                        if ($mergedRows.ContainsKey($existingDateKey)) { throw "Duplicate existing daily bar for $symbol on $existingDateKey." }
                        $mergedRows[$existingDateKey] = [PSCustomObject]@{ Date = $existingDate; Line = $line; Source = "Existing LEAN data" }
                    }
                }
            }
            finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }

        $rows = @($mergedRows.Values | Sort-Object Date)
        $configuredRows = @($rows | Where-Object { $_.Date -ge $StartDate.Date -and $_.Date -lt $EndDateExclusive.Date })
        if ($configuredRows.Count -lt 1900 -or $configuredRows[0].Date -gt [datetime]::new(2018, 1, 5) -or $configuredRows[-1].Date -lt [datetime]::new(2025, 12, 30)) {
            throw "Merged history for $symbol does not cover the configured 2018-2025 period."
        }
        $staged[$symbol] = [PSCustomObject]@{ Path = $archivePath; Rows = $rows; AlpacaBars = $bars.Count; First = $rows[0].Date; Last = $rows[-1].Date }
    }

    $backupDirectory = Join-Path $env:LOCALAPPDATA ("AlgoryLean/data-backup/" + [datetime]::UtcNow.ToString("yyyyMMdd-HHmmss", [Globalization.CultureInfo]::InvariantCulture))
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    foreach ($symbol in $staged.Keys) {
        $archivePath = $staged[$symbol].Path
        $backupPath = Join-Path $backupDirectory (Split-Path $archivePath -Leaf)
        Copy-Item -LiteralPath $archivePath -Destination $backupPath
        $backups[$archivePath] = $backupPath
    }

    try {
        foreach ($symbol in $staged.Keys) {
            $item = $staged[$symbol]
            $temporaryPath = "$($item.Path).alpaca.tmp"
            $temporaryFiles += $temporaryPath
            if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
            $zip = [System.IO.Compression.ZipFile]::Open($temporaryPath, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                $zipEntry = $zip.CreateEntry("$($symbol.ToLowerInvariant()).csv", [System.IO.Compression.CompressionLevel]::Optimal)
                $stream = $zipEntry.Open()
                $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
                try { foreach ($row in $item.Rows) { $writer.WriteLine($row.Line) } }
                finally { $writer.Dispose() }
            }
            finally { $zip.Dispose() }
            Move-Item -LiteralPath $temporaryPath -Destination $item.Path -Force
        }
    }
    catch {
        foreach ($archivePath in $backups.Keys) { Copy-Item -LiteralPath $backups[$archivePath] -Destination $archivePath -Force }
        throw
    }

    $summary = @(foreach ($symbol in ($staged.Keys | Sort-Object)) {
        $item = $staged[$symbol]
        [PSCustomObject]@{ Symbol = $symbol; AlpacaIexBars = $item.AlpacaBars; TotalRows = $item.Rows.Count; FirstDate = $item.First.ToString("yyyy-MM-dd"); LastDate = $item.Last.ToString("yyyy-MM-dd"); Archive = $item.Path }
    })
    $summary | Format-Table -AutoSize
    Write-Output "Original archives backed up to: $backupDirectory"
}
finally {
    foreach ($temporaryPath in $temporaryFiles) {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
    if ($null -ne $headers) { $headers.Clear() }
    if ($keyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer) }
    if ($secretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer) }
    if ($null -ne $keySecure) { $keySecure.Dispose() }
    if ($null -ne $secretSecure) { $secretSecure.Dispose() }
    $storedCredentials = $null
}

