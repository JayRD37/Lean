param(
    [string] $LeanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string] $OutputRoot = (Join-Path $env:TEMP 'lean-multitimeframe-search'),
    [string] $DotnetPath = 'G:\DevTools\LeanSDK\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$launcherDir = Join-Path $LeanRoot 'Optimizer.Launcher\bin\Debug'
$launcher = Join-Path $launcherDir 'QuantConnect.Optimizer.Launcher.dll'
$configSource = Join-Path $LeanRoot 'examples\genetic-discovery\optimizer-smoke.json'
if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
foreach ($path in @($launcher, $configSource)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required optimizer artifact not found: $path" }
}

$runRoot = Join-Path $OutputRoot ([guid]::NewGuid().ToString('N'))
$resultsRoot = Join-Path $runRoot 'results'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$config = Get-Content -LiteralPath $configSource -Raw | ConvertFrom-Json -AsHashtable
$config['results-destination-folder'] = $resultsRoot
$settings = $config['optimization-strategy-settings']
$settings['population-size'] = 8
$settings['random-baseline-size'] = 2
$settings['allowed-timeframes'] = 'Hour,Daily'
$settings['enable-multi-timeframe-confirmation'] = $true
$settings['parameter-robustness-probe-count'] = 0
$configPath = Join-Path $runRoot 'optimizer.json'
[IO.File]::WriteAllText($configPath, (ConvertTo-Json -InputObject $config -Depth 100))

$processInfo = [Diagnostics.ProcessStartInfo]::new()
$processInfo.FileName = $DotnetPath
$processInfo.WorkingDirectory = $launcherDir
$processInfo.ArgumentList.Add($launcher)
$processInfo.ArgumentList.Add('--config')
$processInfo.ArgumentList.Add($configPath)
$processInfo.RedirectStandardOutput = $true
$processInfo.RedirectStandardError = $true
$processInfo.UseShellExecute = $false
$process = [Diagnostics.Process]::new()
$process.StartInfo = $processInfo
if (-not $process.Start()) { throw 'Could not start the LEAN optimizer.' }
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
[IO.File]::WriteAllText((Join-Path $runRoot 'optimizer.log'), $stdoutTask.Result + [Environment]::NewLine + $stderrTask.Result)
if ($process.ExitCode -ne 0) { throw "LEAN optimizer exited $($process.ExitCode). See $(Join-Path $runRoot 'optimizer.log')" }

$historyPath = Join-Path $resultsRoot 'genetic-search-history.jsonl'
if (-not (Test-Path -LiteralPath $historyPath)) { throw "Genetic search history was not created: $historyPath" }
$history = @(Get-Content -LiteralPath $historyPath | ForEach-Object { $_ | ConvertFrom-Json })
$confirmed = @($history | Where-Object { $_.phase -eq 0 -and $_.Genome.ConfirmationTimeframe -eq 2 })
if ($confirmed.Count -eq 0) { throw 'The seeded search generated no training candidate with daily confirmation.' }

$verified = $false
foreach ($candidate in $confirmed) {
    $candidateLog = Get-ChildItem -LiteralPath $resultsRoot -Recurse -Filter "$($candidate.BacktestId)-log.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $candidateLog) { continue }
    $logBody = Get-Content -LiteralPath $candidateLog.FullName -Raw
    if ($logBody -match 'Multi-timeframe confirmation ready: symbol=SPY primary=Hour confirmation=Daily' -and
        $logBody -notmatch 'Execution safety gate tripped') {
        $verified = $true
        break
    }
}
if (-not $verified) { throw 'No GA-generated daily-confirmation candidate reached LEAN indicator readiness without an invalid-order safety trip.' }

Write-Output "PASS: LEAN optimizer completed $($history.Count) candidate backtests; $($confirmed.Count) training genomes used daily confirmation."
Write-Output 'PASS: A GA-generated hourly/daily candidate reached confirmation readiness without an execution safety trip.'
Write-Output "Run artifacts: $runRoot"

