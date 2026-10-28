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

function Run-Backtest([string] $Name, [string] $StartDate, [string] $EndDate, [string] $Symbol, [string] $Timeframe = 'Daily', [string] $ConfirmationTimeframe = 'None', [int] $MaxPositions = 1, [int] $MaxExposurePercent = 50, [double] $MaximumCorrelation = 0.85, [bool] $AuditPortfolioSelection = $false) {
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
        'discovery.timeframe' = $Timeframe
        'discovery.confirmation-timeframe' = $ConfirmationTimeframe
        'discovery.family' = 'TrendFollowing'
        'discovery.fast-period' = '10'
        'discovery.slow-period' = '40'
        'discovery.entry-tenths' = '12'
        'discovery.exit-tenths' = '4'
        'discovery.max-positions' = [string]$MaxPositions
        'discovery.max-exposure-percent' = [string]$MaxExposurePercent
        'discovery.minimum-spread-bps' = '5'
        'discovery.max-correlation' = $MaximumCorrelation.ToString([Globalization.CultureInfo]::InvariantCulture)
        'discovery.max-drawdown' = '0.50'
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
$corporateActions = Run-Backtest 'corporate-actions' '2020-07-01' '2020-11-10' 'AAPL'
$multiAsset = Run-Backtest 'multi-asset' '2019-01-01' '2021-03-31' 'SPY|QQQ|IWM' 'Daily' 'None' 3 60 0.99 $true
$multiShort = Run-Backtest 'multi-prefix' '2020-01-02' '2021-02-15' 'SPY' 'Hour' 'Daily'
$multiLong = Run-Backtest 'multi-full-history' '2020-01-02' '2021-03-31' 'SPY' 'Hour' 'Daily'
Run-RawSplitCheck | Out-Null
$shortOrders = @(Get-NormalizedOrders $short.Result $through)
$longOrders = @(Get-NormalizedOrders $long.Result $through)
$multiShortOrders = @(Get-NormalizedOrders $multiShort.Result $through)
$multiLongOrders = @(Get-NormalizedOrders $multiLong.Result $through)
$multiAssetSymbols = @((Get-NormalizedOrders $multiAsset.Result ([datetime]'2021-03-31')) | Select-Object -ExpandProperty Symbol -Unique)

if ($shortOrders.Count -eq 0) { throw "Prefix run produced no filled orders through $($through.ToString('yyyy-MM-dd')). Inspect $($short.LogPath)" }
$shortJson = ConvertTo-Json -InputObject $shortOrders -Depth 5 -Compress
$longJson = ConvertTo-Json -InputObject $longOrders -Depth 5 -Compress
if ($shortJson -cne $longJson) {
    $shortOrders | Export-Csv (Join-Path $OutputRoot 'prefix-orders.csv') -NoTypeInformation
    $longOrders | Export-Csv (Join-Path $OutputRoot 'full-history-orders.csv') -NoTypeInformation
    throw "Prefix/full history orders differ through $($through.ToString('yyyy-MM-dd')); inspect the CSVs under $OutputRoot"
}

Write-Output "PASS: $($shortOrders.Count) filled orders are identical through $($through.ToString('yyyy-MM-dd'))."
$multiShortJson = ConvertTo-Json -InputObject $multiShortOrders -Depth 5 -Compress
$multiLongJson = ConvertTo-Json -InputObject $multiLongOrders -Depth 5 -Compress
if ($multiShortOrders.Count -eq 0) { throw "Multi-timeframe prefix run produced no filled orders through $($through.ToString('yyyy-MM-dd')). Inspect $($multiShort.LogPath)" }
if ($multiShortJson -cne $multiLongJson) { throw "Multi-timeframe prefix/full-history orders differ through $($through.ToString('yyyy-MM-dd')); inspect the run artifacts under $OutputRoot" }
Write-Output "PASS: $($multiShortOrders.Count) hourly/daily-confirmation fills are identical through $($through.ToString('yyyy-MM-dd'))."
$selectionAudit = Get-Content -LiteralPath $multiAsset.AlgorithmLogPath | Where-Object { $_ -match 'Portfolio selection audit:' }
if ($multiAssetSymbols.Count -lt 2) { throw "The multi-asset backtest filled fewer than two configured symbols; inspect $($multiAsset.AlgorithmLogPath)" }
if ($selectionAudit.Count -eq 0) { throw "No portfolio selection audit records were emitted for the multi-asset run; inspect $($multiAsset.AlgorithmLogPath)" }
foreach ($line in $selectionAudit) {
    if ($line -match 'positionCount=(\d+)' -and [int]$Matches[1] -gt 3) { throw "Portfolio selection exceeded the three-position cap: $line" }
    if ($line -match 'grossTarget=([0-9.]+)%' -and [decimal]$Matches[1] -gt 60) { throw "Portfolio selection exceeded the 60% gross-exposure cap: $line" }
}
Write-Output "PASS: multi-asset LEAN backtest filled $($multiAssetSymbols.Count) tickers; every audited target respected 3 positions and 60% gross exposure."
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





