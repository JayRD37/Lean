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

function Run-Backtest([string] $Name, [string] $StartDate, [string] $EndDate, [string] $Symbol) {
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
        'discovery.timeframe' = 'Daily'
        'discovery.family' = 'TrendFollowing'
        'discovery.fast-period' = '10'
        'discovery.slow-period' = '40'
        'discovery.entry-tenths' = '12'
        'discovery.exit-tenths' = '4'
        'discovery.max-positions' = '1'
        'discovery.max-exposure-percent' = '50'
        'discovery.minimum-spread-bps' = '5'
        'discovery.max-correlation' = '0.85'
        'discovery.max-drawdown' = '0.50'
        'discovery.max-negative-review-periods' = '100'
        'discovery.retirement-drawdown' = '0.99'
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
    if ($actionsLog -notmatch 'dividend AAPL distribution=') { throw "Raw-mode dividend event was not delivered; inspect $algorithmLogPath" }
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
Run-RawSplitCheck | Out-Null
$shortOrders = @(Get-NormalizedOrders $short.Result $through)
$longOrders = @(Get-NormalizedOrders $long.Result $through)

if ($shortOrders.Count -eq 0) { throw "Prefix run produced no filled orders through $($through.ToString('yyyy-MM-dd')). Inspect $($short.LogPath)" }
$shortJson = ConvertTo-Json -InputObject $shortOrders -Depth 5 -Compress
$longJson = ConvertTo-Json -InputObject $longOrders -Depth 5 -Compress
if ($shortJson -cne $longJson) {
    $shortOrders | Export-Csv (Join-Path $OutputRoot 'prefix-orders.csv') -NoTypeInformation
    $longOrders | Export-Csv (Join-Path $OutputRoot 'full-history-orders.csv') -NoTypeInformation
    throw "Prefix/full history orders differ through $($through.ToString('yyyy-MM-dd')); inspect the CSVs under $OutputRoot"
}

Write-Output "PASS: $($shortOrders.Count) filled orders are identical through $($through.ToString('yyyy-MM-dd'))."
$actionsLog = Get-Content -LiteralPath $corporateActions.AlgorithmLogPath -Raw
if ($actionsLog -notmatch 'Corporate action: split symbol=AAPL type=SplitOccurred factor=0\.25') { throw "LEAN did not deliver AAPL's 2020 four-for-one split event; inspect $($corporateActions.AlgorithmLogPath)" }
$splitLine = ($actionsLog -split "`r?`n") | Where-Object { $_ -match 'Corporate action: split symbol=AAPL type=SplitOccurred factor=0\.25' } | Select-Object -Last 1
if ($splitLine -notmatch 'holdingQuantity=(-?[0-9.]+)' -or [decimal]$Matches[1] -eq 0) { throw "AAPL had no open position during the split event; inspect $($corporateActions.AlgorithmLogPath)" }
if ($actionsLog -notmatch 'Corporate action: dividend symbol=AAPL distribution=') { throw "LEAN did not deliver an AAPL dividend event; inspect $($corporateActions.AlgorithmLogPath)" }
Write-Output "PASS: LEAN delivered AAPL split and dividend events through the adjusted-data strategy run."
Write-Output "PASS: LEAN adjusted one AAPL share to four and delivered a dividend under raw normalization."
Write-Output "Prefix result: $($short.ResultPath)"
Write-Output "Full-history result: $($long.ResultPath)"
Write-Output "Persistent drawdown state verified in both runs."
Write-Output "Run artifacts: $OutputRoot"


