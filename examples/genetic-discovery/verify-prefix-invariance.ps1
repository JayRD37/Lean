param(
    [string] $LeanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string] $OutputRoot = (Join-Path $env:TEMP 'lean-prefix-invariance'),
    [string] $DotnetPath = 'G:\DevTools\LeanSDK\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$launcherDir = Join-Path $LeanRoot 'Launcher\bin\Debug'
$launcher = Join-Path $launcherDir 'QuantConnect.Lean.Launcher.dll'
$algorithm = Join-Path $launcherDir 'QuantConnect.Algorithm.CSharp.dll'
$baseConfig = Join-Path $LeanRoot 'Launcher\config.json'
$dataDir = Join-Path $LeanRoot 'Data'
if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$dotnet = $DotnetPath

foreach ($required in @($launcher, $algorithm, $baseConfig, $dataDir)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required LEAN artifact not found: $required" }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Add-Type -Path (Join-Path $launcherDir 'Newtonsoft.Json.dll')

function Read-JsonWithComments([string] $Path) {
    $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StreamReader]::new($Path))
    try {
        $settings = [Newtonsoft.Json.Linq.JsonLoadSettings]::new()
        $settings.CommentHandling = [Newtonsoft.Json.Linq.CommentHandling]::Ignore
        return [Newtonsoft.Json.Linq.JObject]::Load($reader, $settings).ToString()
    }
    finally { $reader.Close() }
}

function Run-Backtest([string] $Name, [string] $StartDate, [string] $EndDate, [string] $Symbol, [string] $Timeframe = 'Daily', [string] $ConfirmationTimeframe = 'None', [int] $MaxPositions = 1, [int] $MaxExposurePercent = 50, [double] $MaximumCorrelation = 0.85, [bool] $AuditPortfolioSelection = $false, [decimal] $MinimumSpreadBps = 5, [string] $UniverseMode = 'Configured', [int] $UniverseSize = 20, [decimal] $MaximumDrawdown = 0.50) {
    $runRoot = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $config = ConvertFrom-Json -InputObject (Read-JsonWithComments $baseConfig) -AsHashtable
    $config['environment'] = 'backtesting'
    $config['algorithm-type-name'] = 'GeneticDiscoveryAlgorithm'
    $config['algorithm-location'] = $algorithm
    $config['data-folder'] = $dataDir
    $config['results-destination-folder'] = $runRoot
    $params = @{}
    $config['parameters'] = $params
    $values = @{
        'discovery.phase' = 'Training'
        'discovery.train-start' = $StartDate
        'discovery.train-end' = $EndDate
        'discovery.symbols' = $Symbol
        'discovery.universe-mode' = $UniverseMode
        'discovery.universe-size' = [string]$UniverseSize
        'discovery.timeframe' = $Timeframe
        'discovery.confirmation-timeframe' = $ConfirmationTimeframe
        'discovery.family' = 'TrendFollowing'
        'discovery.fast-period' = '10'
        'discovery.slow-period' = '40'
        'discovery.entry-tenths' = '12'
        'discovery.exit-tenths' = '4'
        'discovery.max-positions' = [string]$MaxPositions
        'discovery.max-exposure-percent' = [string]$MaxExposurePercent
        'discovery.minimum-spread-bps' = $MinimumSpreadBps.ToString([Globalization.CultureInfo]::InvariantCulture)
        'discovery.max-correlation' = $MaximumCorrelation.ToString([Globalization.CultureInfo]::InvariantCulture)
        'discovery.max-drawdown' = $MaximumDrawdown.ToString([Globalization.CultureInfo]::InvariantCulture)
        'discovery.max-negative-review-periods' = '100'
        'discovery.retirement-drawdown' = '0.99'
        'discovery.audit-portfolio-selection' = $AuditPortfolioSelection.ToString().ToLowerInvariant()
        'discovery.kill-switch-file' = (Join-Path $runRoot 'never-trigger')
        'discovery.drawdown-state-file' = (Join-Path $runRoot 'portfolio-drawdown-state.json')
    }
    foreach ($key in $values.Keys) { $params[$key] = [string]$values[$key] }

    $configPath = Join-Path $runRoot 'config.json'
    $serializedConfig = ConvertTo-Json -InputObject $config -Depth 100
    [IO.File]::WriteAllText($configPath, $serializedConfig)
    $logPath = Join-Path $runRoot 'launcher.log'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = $launcherDir
    $start.ArgumentList.Add($launcher)
    $start.ArgumentList.Add('--config')
    $start.ArgumentList.Add($configPath)
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start LEAN for $Name" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $log = $stdoutTask.Result + [Environment]::NewLine + $stderrTask.Result
    [IO.File]::WriteAllText($logPath, $log)
    if ($process.ExitCode -ne 0) { throw "LEAN run '$Name' exited $($process.ExitCode). See $logPath" }

    $resultPath = Join-Path $runRoot 'GeneticDiscoveryAlgorithm.json'
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "No backtest result JSON found under $runRoot. See $logPath" }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $drawdownPath = Join-Path $runRoot 'portfolio-drawdown-state.json'
    if (-not (Test-Path -LiteralPath $drawdownPath)) { throw "Persistent drawdown state was not created under $runRoot. See $logPath" }
    $drawdown = Get-Content -LiteralPath $drawdownPath -Raw | ConvertFrom-Json
    if ($drawdown.Version -ne 1 -or [decimal]$drawdown.HighWatermark -le 0) { throw "Persistent drawdown state is invalid at $drawdownPath" }
    $algorithmLogPath = Join-Path $runRoot 'GeneticDiscoveryAlgorithm-log.txt'
    if (-not (Test-Path -LiteralPath $algorithmLogPath)) { throw "Algorithm log not found under $runRoot" }
    return [pscustomobject]@{ Result = $result; ResultPath = $resultPath; LogPath = $logPath; AlgorithmLogPath = $algorithmLogPath; DrawdownPath = $drawdownPath }
}

function Run-RawSplitCheck {
    $runRoot = Join-Path $OutputRoot 'raw-split-check'
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $config = ConvertFrom-Json -InputObject (Read-JsonWithComments $baseConfig) -AsHashtable
    $config['environment'] = 'backtesting'
    $config['algorithm-type-name'] = 'GeneticDiscoveryCorporateActionAlgorithm'
    $config['algorithm-location'] = $algorithm
    $config['data-folder'] = $dataDir
    $config['results-destination-folder'] = $runRoot
    $config['parameters'] = @{}

    $configPath = Join-Path $runRoot 'config.json'
    [IO.File]::WriteAllText($configPath, (ConvertTo-Json -InputObject $config -Depth 100))
    $logPath = Join-Path $runRoot 'launcher.log'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = $launcherDir
    $start.ArgumentList.Add($launcher)
    $start.ArgumentList.Add('--config')
    $start.ArgumentList.Add($configPath)
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Could not start LEAN for raw-mode split check.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $log = $stdoutTask.Result + [Environment]::NewLine + $stderrTask.Result
    [IO.File]::WriteAllText($logPath, $log)
    if ($process.ExitCode -ne 0) { throw "Raw-mode split check failed. See $logPath" }

    $algorithmLogPath = Join-Path $runRoot 'GeneticDiscoveryCorporateActionAlgorithm-log.txt'
    if (-not (Test-Path -LiteralPath $algorithmLogPath)) { throw "Raw-mode split check log not found: $algorithmLogPath" }
    $actionsLog = Get-Content -LiteralPath $algorithmLogPath -Raw
    if ($actionsLog -notmatch 'split AAPL factor=0\.25 holdingQuantity=4') { throw "Raw-mode split did not adjust one AAPL share to four; inspect $algorithmLogPath" }
    if ($actionsLog -notmatch 'dividend AAPL distribution=.* credited=[0-9.]+') { throw "Raw-mode dividend was not credited to the held position; inspect $algorithmLogPath" }
    return $algorithmLogPath
}

function Get-NormalizedOrders($Result, [datetime] $Through) {
    @($Result.orders.PSObject.Properties | ForEach-Object { $_.Value } |
        Where-Object {
            $_.status -eq 3 -and
            [datetime]::Parse($_.lastFillTime).Date -le $Through.Date
        } |
        ForEach-Object {
            [pscustomobject]@{
                Time = ([datetime]::Parse($_.lastFillTime)).ToString('yyyy-MM-dd')
                Symbol = $_.symbol.value
                Quantity = [decimal]$_.quantity
                Price = [decimal]$_.price
            }
        } | Sort-Object Time, Symbol, Quantity, Price)
}

$through = [datetime]'2021-02-10'
$short = Run-Backtest 'prefix' '2019-01-01' '2021-02-15' 'SPY'
$long = Run-Backtest 'full-history' '2019-01-01' '2021-03-31' 'SPY'
$costStress = Run-Backtest 'cost-stress' '2019-01-01' '2021-02-15' 'SPY' 'Daily' 'None' 1 50 0.85 $false 200
$corporateActions = Run-Backtest 'corporate-actions' '2020-07-01' '2020-11-10' 'AAPL'
$pointInTimeUniverseShort = Run-Backtest 'point-in-time-universe-prefix' '2014-03-24' '2014-04-03' 'SPY' 'Daily' 'None' 3 60 0.99 $true 5 'TopDollarVolume' 3
$pointInTimeUniverse = Run-Backtest 'point-in-time-universe' '2014-03-24' '2014-04-07' 'SPY' 'Daily' 'None' 3 60 0.99 $true 5 'TopDollarVolume' 3
$multiAssetShort = Run-Backtest 'multi-asset-prefix' '2019-01-01' '2021-02-15' 'SPY|QQQ|IWM' 'Daily' 'None' 3 60 0.99 $true
$multiAsset = Run-Backtest 'multi-asset' '2019-01-01' '2021-03-31' 'SPY|QQQ|IWM' 'Daily' 'None' 3 60 0.99 $true
$multiShort = Run-Backtest 'multi-prefix' '2020-01-02' '2021-02-15' 'SPY' 'Hour' 'Daily'
$multiLong = Run-Backtest 'multi-full-history' '2020-01-02' '2021-03-31' 'SPY' 'Hour' 'Daily'
$drawdownRun = Run-Backtest -Name 'drawdown-shutdown' -StartDate '2019-01-01' -EndDate '2020-05-01' -Symbol 'SPY' -MaximumDrawdown 0.05
Run-RawSplitCheck | Out-Null
$shortOrders = @(Get-NormalizedOrders $short.Result $through)
$longOrders = @(Get-NormalizedOrders $long.Result $through)
$multiShortOrders = @(Get-NormalizedOrders $multiShort.Result $through)
$multiLongOrders = @(Get-NormalizedOrders $multiLong.Result $through)
$multiAssetShortOrders = @(Get-NormalizedOrders $multiAssetShort.Result $through)
$multiAssetLongOrders = @(Get-NormalizedOrders $multiAsset.Result $through)
$universeThrough = [datetime]'2014-04-02'
$pointInTimeUniverseShortOrders = @(Get-NormalizedOrders $pointInTimeUniverseShort.Result $universeThrough)
$pointInTimeUniverseLongOrders = @(Get-NormalizedOrders $pointInTimeUniverse.Result $universeThrough)
$multiAssetSymbols = @((Get-NormalizedOrders $multiAsset.Result ([datetime]'2021-03-31')) | Select-Object -ExpandProperty Symbol -Unique)

if ($shortOrders.Count -eq 0) { throw "Prefix run produced no filled orders through $($through.ToString('yyyy-MM-dd')). Inspect $($short.LogPath)" }
$drawdownState = Get-Content -LiteralPath $drawdownRun.DrawdownPath -Raw | ConvertFrom-Json
if (-not $drawdownState.Tripped) { throw "Historical SPY drawdown did not trip the persistent account guard; inspect $($drawdownRun.AlgorithmLogPath)" }
$drawdownOrders = @($drawdownRun.Result.Orders.PSObject.Properties | ForEach-Object { $_.Value } | Where-Object { $_.status -eq 3 })
$drawdownEntries = @($drawdownOrders | Where-Object { $_.tag -notmatch 'drawdown or kill switch' -and [decimal]$_.quantity -gt 0 })
$drawdownExits = @($drawdownOrders | Where-Object { $_.tag -match 'drawdown or kill switch' })
if ($drawdownEntries.Count -eq 0 -or $drawdownExits.Count -eq 0) { throw "Drawdown guard did not demonstrate entry followed by risk liquidation; inspect $($drawdownRun.AlgorithmLogPath)" }
$drawdownNetQuantity = ($drawdownOrders | Measure-Object -Property quantity -Sum).Sum
if ([math]::Abs([decimal]$drawdownNetQuantity) -gt [decimal]0.000001) { throw "Drawdown shutdown ended with a non-flat managed position ($drawdownNetQuantity shares); inspect $($drawdownRun.AlgorithmLogPath)" }
$firstDrawdownExit = ($drawdownExits | ForEach-Object { [datetime]::Parse($_.lastFillTime) } | Measure-Object -Minimum).Minimum
if (@($drawdownOrders | Where-Object { [datetime]::Parse($_.lastFillTime) -gt $firstDrawdownExit -and $_.tag -notmatch 'drawdown or kill switch' }).Count -gt 0) { throw "Strategy submitted non-liquidation orders after its drawdown trip; inspect $($drawdownRun.AlgorithmLogPath)" }
Write-Output "PASS: LEAN drawdown guard latched, liquidated the SPY holding, persisted the trip, and prevented later strategy orders."
$shortJson = ConvertTo-Json -InputObject $shortOrders -Depth 5 -Compress
$longJson = ConvertTo-Json -InputObject $longOrders -Depth 5 -Compress
if ($shortJson -cne $longJson) {
    $shortOrders | Export-Csv (Join-Path $OutputRoot 'prefix-orders.csv') -NoTypeInformation
    $longOrders | Export-Csv (Join-Path $OutputRoot 'full-history-orders.csv') -NoTypeInformation
    throw "Prefix/full history orders differ through $($through.ToString('yyyy-MM-dd')); inspect the CSVs under $OutputRoot"
}

Write-Output "PASS: $($shortOrders.Count) filled orders are identical through $($through.ToString('yyyy-MM-dd'))."
$estimatedFeesText = $short.Result.Statistics.'Total Fees' -replace '[^0-9.-]', ''
$estimatedFees = [decimal]::Parse($estimatedFeesText, [Globalization.CultureInfo]::InvariantCulture)
if ($estimatedFees -le 0) { throw "The Alpaca US equity fee model did not charge regulatory fees in the prefix run. Inspect $($short.LogPath)" }
Write-Output "PASS: Alpaca equity regulatory fees were included in the LEAN backtest ($($short.Result.Statistics.'Total Fees'))."
$baselineNetProfit = [double]::Parse(($short.Result.Statistics.'Net Profit' -replace '%', ''), [Globalization.CultureInfo]::InvariantCulture)
$stressNetProfit = [double]::Parse(($costStress.Result.Statistics.'Net Profit' -replace '%', ''), [Globalization.CultureInfo]::InvariantCulture)
if ($stressNetProfit -ge $baselineNetProfit) { throw "LEAN's 200 bps full-spread stress did not reduce net profit (baseline=$baselineNetProfit%, stress=$stressNetProfit%). Inspect $($costStress.LogPath)" }
Write-Output "PASS: LEAN applied the configured spread/slippage in the fill path; net profit fell from $baselineNetProfit% at 5 bps to $stressNetProfit% at 200 bps."
$multiShortJson = ConvertTo-Json -InputObject $multiShortOrders -Depth 5 -Compress
$multiLongJson = ConvertTo-Json -InputObject $multiLongOrders -Depth 5 -Compress
if ($multiShortOrders.Count -eq 0) { throw "Multi-timeframe prefix run produced no filled orders through $($through.ToString('yyyy-MM-dd')). Inspect $($multiShort.LogPath)" }
if ($multiShortJson -cne $multiLongJson) { throw "Multi-timeframe prefix/full-history orders differ through $($through.ToString('yyyy-MM-dd')); inspect the run artifacts under $OutputRoot" }
Write-Output "PASS: $($multiShortOrders.Count) hourly/daily-confirmation fills are identical through $($through.ToString('yyyy-MM-dd'))."
$multiAssetShortJson = ConvertTo-Json -InputObject $multiAssetShortOrders -Depth 5 -Compress
$multiAssetLongJson = ConvertTo-Json -InputObject $multiAssetLongOrders -Depth 5 -Compress
if ($multiAssetShortOrders.Count -eq 0) { throw "Multi-asset prefix run produced no filled orders through $($through.ToString('yyyy-MM-dd')). Inspect $($multiAssetShort.LogPath)" }
if ($multiAssetShortJson -cne $multiAssetLongJson) { throw "Multi-asset prefix/full-history orders differ through $($through.ToString('yyyy-MM-dd')); inspect the run artifacts under $OutputRoot" }
Write-Output "PASS: $($multiAssetShortOrders.Count) multi-asset fills are identical through $($through.ToString('yyyy-MM-dd'))."
$selectionAudit = Get-Content -LiteralPath $multiAsset.AlgorithmLogPath | Where-Object { $_ -match 'Portfolio selection audit:' }
if ($multiAssetSymbols.Count -lt 2) { throw "The multi-asset backtest filled fewer than two configured symbols; inspect $($multiAsset.AlgorithmLogPath)" }
if ($selectionAudit.Count -eq 0) { throw "No portfolio selection audit records were emitted for the multi-asset run; inspect $($multiAsset.AlgorithmLogPath)" }
foreach ($line in $selectionAudit) {
    if ($line -match 'positionCount=(\d+)' -and [int]$Matches[1] -gt 3) { throw "Portfolio selection exceeded the three-position cap: $line" }
    if ($line -match 'grossTarget=([0-9.]+)%' -and [decimal]$Matches[1] -gt 60) { throw "Portfolio selection exceeded the 60% gross-exposure cap: $line" }
}
Write-Output "PASS: multi-asset LEAN backtest filled $($multiAssetSymbols.Count) tickers; every audited target respected 3 positions and 60% gross exposure."
$universeLog = Get-Content -LiteralPath $pointInTimeUniverse.AlgorithmLogPath -Raw
if ($universeLog -notmatch 'Point-in-time coarse universe selected [1-3] securities from [0-9]+ records') { throw "The historical coarse-data universe did not select any symbols; inspect $($pointInTimeUniverse.AlgorithmLogPath)" }
if ($universeLog -notmatch 'Point-in-time coarse universe selected [1-3] securities from [0-9]+ records.*AAPL') { throw "The 2014 point-in-time coarse universe did not select AAPL from that date's local coarse data; inspect $($pointInTimeUniverse.AlgorithmLogPath)" }
if ($universeLog -match 'Execution safety gate tripped') { throw "The point-in-time universe run hit the execution safety gate; inspect $($pointInTimeUniverse.AlgorithmLogPath)" }
$selectionSets = @((($universeLog -split "`r?`n") | Where-Object { $_ -match 'Point-in-time coarse universe selected .*: (.+)$' } | ForEach-Object { $Matches[1] }) | Sort-Object -Unique)
if ($selectionSets.Count -lt 2) { throw "The historical coarse universe did not change its selected members; inspect $($pointInTimeUniverse.AlgorithmLogPath)" }
$universeShortJson = ConvertTo-Json -InputObject $pointInTimeUniverseShortOrders -Depth 5 -Compress
$universeLongJson = ConvertTo-Json -InputObject $pointInTimeUniverseLongOrders -Depth 5 -Compress
if ($pointInTimeUniverseShortOrders.Count -eq 0 -or $universeShortJson -cne $universeLongJson) { throw "Point-in-time universe fills differ between short and longer histories through $($universeThrough.ToString('yyyy-MM-dd')); inspect the run artifacts under $OutputRoot" }
$universeFilledSymbols = @((Get-NormalizedOrders $pointInTimeUniverse.Result ([datetime]'2014-04-07')) | Select-Object -ExpandProperty Symbol -Unique)
if (@($universeFilledSymbols | Where-Object { $_ -ne 'SPY' }).Count -eq 0) { throw "The point-in-time universe run did not fill an order for a dynamically selected security; inspect $($pointInTimeUniverse.AlgorithmLogPath)" }
Write-Output "PASS: $($pointInTimeUniverseShortOrders.Count) fills from the point-in-time universe matched through $($universeThrough.ToString('yyyy-MM-dd')); LEAN filled dynamic security symbols ($($universeFilledSymbols -join ','))."
$actionsLog = Get-Content -LiteralPath $corporateActions.AlgorithmLogPath -Raw
if ($actionsLog -notmatch 'Corporate action: split symbol=AAPL type=SplitOccurred factor=0\.25') { throw "LEAN did not deliver AAPL's 2020 four-for-one split event; inspect $($corporateActions.AlgorithmLogPath)" }
$splitLine = ($actionsLog -split "`r?`n") | Where-Object { $_ -match 'Corporate action: split symbol=AAPL type=SplitOccurred factor=0\.25' } | Select-Object -Last 1
if ($splitLine -notmatch 'holdingQuantity=(-?[0-9.]+)' -or [decimal]$Matches[1] -eq 0) { throw "AAPL had no open position during the split event; inspect $($corporateActions.AlgorithmLogPath)" }
if ($actionsLog -notmatch 'Corporate action: dividend symbol=AAPL distribution=') { throw "LEAN did not deliver an AAPL dividend event; inspect $($corporateActions.AlgorithmLogPath)" }
Write-Output "PASS: LEAN delivered AAPL split and dividend events through the adjusted-data strategy run."
$multiTimeframeLog = Get-Content -LiteralPath $multiLong.AlgorithmLogPath -Raw
if ($multiTimeframeLog -notmatch 'Multi-timeframe confirmation ready: symbol=SPY primary=Hour confirmation=Daily') { throw "Daily confirmation indicators never became ready on the hourly SPY subscription; inspect $($multiLong.AlgorithmLogPath)" }
if ($multiTimeframeLog -match 'Execution safety gate tripped') { throw "Multi-timeframe backtest hit an invalid order; inspect $($multiLong.AlgorithmLogPath)" }
Write-Output "PASS: LEAN aggregated daily confirmation indicators over hourly SPY data and reached readiness."
Write-Output "PASS: LEAN adjusted one AAPL share to four and credited a dividend under raw normalization."
Write-Output "Prefix result: $($short.ResultPath)"
Write-Output "Full-history result: $($long.ResultPath)"
Write-Output "Persistent drawdown state verified in both runs."
Write-Output "Run artifacts: $OutputRoot"





