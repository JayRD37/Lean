# Genetic strategy discovery for LEAN

This branch adds an independent, deterministic genetic search module and connects it to LEAN's existing optimizer and backtest runner. The Algory investigation remains behavioral research only; no Algory executable, service, protected code, or license mechanism is used here.

The repository baseline is upstream LEAN commit `22404ea28b8d3f3c9eb74d6ed9f0444426f8e417` (upstream `master` at the fork point, rechecked on 2026-10-27). The exact commit plus the SDK pin makes the build baseline reproducible.

## Architecture

- `StrategyDiscovery/QuantConnect.Genetic.csproj` is the standalone engine. A genome selects long-only trend-following or mean-reversion logic, minute/hour/daily resolution, moving-average and volatility periods, entry/exit thresholds, position count, and gross exposure.
- `Optimizer/Strategies/GeneticSearchOptimizationStrategy.cs` implements LEAN's `IOptimizationStrategy`. It submits candidate parameter sets to LEAN; `ConsoleLeanOptimizer` starts independent LEAN processes and bounds concurrency. Growth and Consistency populations evolve separately. Random candidates provide a baseline.
- `Algorithm.CSharp/GeneticDiscoveryAlgorithm.cs` evaluates one genome using LEAN securities, indicators, portfolio targets, brokerage fee model, slippage, and drawdown risk model. It budgets remaining position slots and gross exposure after existing outside-universe holdings, and emits zero targets only for its configured symbols when signals disappear or shutdown is requested. A file-based emergency stop is checked on data events.
- Training generations are scored on the configured training dates. Finalists from both evolved populations and random candidates are then rerun across configurable, contiguous chronological windows in the disjoint validation date interval. Evolved finalists also get deterministic one-axis parameter probes around their fast/slow periods and entry/exit thresholds. A finalist must finish every central window, satisfy constraints in every central window, and have valid results for every requested probe. Its score is the lower of central lower-median fitness and neighbor lower-median fitness. JSONL evaluation history and ranked training/validation records are written under LEAN's results folder. The ranking file compares evolved finalists with the median held-out random baseline and records probe count, valid probe count, weakest neighbor fitness, and fitness delta. When LEAN provides closed trades, history also includes a seeded IID bootstrap summary of trade-level returns.
- The example algorithm runs scheduled health reviews every configured number of calendar days, emits a reoptimization request in its log, and retires after repeated negative review periods or a configured drawdown. A separate supervisor still needs to consume that request, launch a new optimizer run, and restart the algorithm with an approved genome.

The engine's seeded generator makes candidate creation and evolution reproducible. Evaluation order does not affect the next generation because breeding begins only after every candidate in that generation has completed. LEAN's optimizer controls process concurrency.

## Build and run a local discovery search

The SDK is pinned in `global.json` to .NET SDK 10.0.403. Build from the repository root, then launch the optimizer with the checked-in config:

```powershell
dotnet build QuantConnect.Lean.sln
Push-Location Optimizer.Launcher/bin/Debug
dotnet QuantConnect.Optimizer.Launcher.dll --config ../../../examples/genetic-discovery/optimizer.json
Pop-Location
```

`optimizer.json` is a modest search example with three chronological validation windows and four parameter probes per evolved finalist. `optimizer-smoke.json` runs ten LEAN backtests on the March 2021 SPY data archive, with one validation window and one probe per evolved finalist. Ensure LEAN's matching US equity data is installed before running. Result and LEAN launcher paths are relative to `Optimizer.Launcher/bin/Debug`.

The current interface accepts a non-empty LEAN optimizer `parameters` list for compatibility, while the genetic strategy itself emits genome parameters. The `discovery.marker` static parameter is a required placeholder and is ignored by the algorithm.

## Local end-to-end discovery smoke run

On 2026-10-28, the local `ConsoleLeanOptimizer` completed ten real LEAN backtests from this branch: five training evaluations, followed by three held-out finalists and two parameter-perturbation evaluations. All ten produced parseable LEAN statistics; the optimizer wrote `genetic-search-history.jsonl` and `genetic-search-rankings.json`, persisted trade-sequence Monte Carlo diagnostics where closed trades existed, compared validation finalists against the random baseline, reported parameter probe outcomes, and selected its solution from the validation phase. The smoke dates were 2021-01-04 through 2021-03-31 because the checked-out SPY daily data ends on 2021-03-31. This verifies the execution path only; the short sample is not useful evidence of strategy quality.

## Alpaca paper execution

`StrategyDiscovery.Supervisor` runs the optimizer on a schedule, requires a fresh ranking file, approves only evolved candidates that pass per-window trade/drawdown gates and probe checks, and starts LEAN using the promoted genome. The supervisor keeps the current paper process running during optimization. For promotion or shutdown it creates the algorithm's emergency-stop file, waits for managed positions to close and LEAN to exit, then starts the replacement. Optimizer failures leave a running paper process untouched. Unexpected paper-process exits use exponential backoff and stop after the configured consecutive-failure limit. Its JSONL audit and child process logs go under the configured output directory.

The supervisor is Alpaca-paper-only. It refuses to start without both the `--enable-paper` command-line switch and `ALGORY_ALPACA_API_KEY` / `ALGORY_ALPACA_API_SECRET` environment variables. It merges `examples/genetic-discovery/paper-config.overlay.json` over LEAN's base config, forces `environment=live-alpaca`, `alpaca-paper-trading=true`, and the algorithm paper gate, and rejects optimizer/paper mismatches in symbols or risk caps. The generated config with credentials is ephemeral under the current user's temporary directory and is deleted after the LEAN child exits normally; a supervisor crash can leave it behind for manual cleanup. The supervisor has no real-money mode.

To build and run the unattended paper supervisor after setting Alpaca **paper** credentials through a secret manager:

```powershell
dotnet build QuantConnect.Lean.sln
dotnet StrategyDiscovery.Supervisor/bin/Debug/QuantConnect.Genetic.Supervisor.dll --config examples/genetic-discovery/supervisor.json --enable-paper
```

`Ctrl+C` requests shutdown through the emergency-stop file. A manually present emergency-stop file is honored and prevents a restart. Running this workflow has not been demonstrated because this environment has no Alpaca paper credentials. Real-money live trading remains disabled by default and is not supported by this supervisor.

## Algory comparison

| Capability | This branch | Algory evidence status |
| --- | --- | --- |
| Candidate generation and genetic evolution | Implemented independently: seeded random candidates, tournament selection, elitism, uniform crossover and mutation | Exact Algory genome/operator details remain opaque in its protected runtime |
| Growth and Consistency populations | Separate configurable fitness functions and evolutionary populations | Names and high-level behavior observed/documented; internal scoring is not known |
| Multiple assets and timeframes | US equity symbols and minute/hour/daily bar resolution | Publicly described behavior; detailed processing is unverified |
| Held-out validation | Configurable contiguous walk-forward windows, finalist and randomized baseline backtests | Validation details beyond observed/documented claims are unverified |
| Backtest engine and brokerage/risk models | LEAN backtester, optimizer, Alpaca brokerage model, risk framework | Independently implemented on LEAN |
| Portfolio-level correlation/exposure limits | Correlation is limited among selected symbols; account position/exposure capacity subtracts existing outside-universe holdings | Same-ticker holdings cannot be attributed to another algorithm, and concurrent algorithms can race; use a dedicated paper account |
| Chronological walk-forward validation | Configurable contiguous windows; all windows must complete and pass constraints; finalist ranked by the lower median window fitness | Windows are calendar-date partitions, not purged or embargoed; deployment still requires a separately approved process |
| Parameter-neighborhood robustness | Deterministic fast/slow period and entry/exit threshold probes run on each evolved finalist across the holdout windows; all probes must produce valid metrics to qualify | Probe count can be set to zero; neighbor testing is sensitivity analysis, not statistical significance |
| Randomized significance analysis | Not implemented yet | Documented or commonly expected research functions; runtime details unverified |
| Monte Carlo analysis | Seeded IID bootstrap of observed closed-trade returns with loss probability, terminal-return quantiles, and drawdown quantile | Independent diagnostic; it assumes trade outcomes can be resampled independently and is not a predictive distribution |
| Persistent rankings | Local JSONL history and JSON ranking output | Algory persistence backend is service-dependent and not established |
| Scheduled reevaluation and strategy decay/retirement | Example algorithm reviews health monthly and retires at drawdown or repeated negative reviews; supervisor reoptimizes every configured interval and after process exit | Supervisor orchestration is implemented but unattended brokerage behavior has not been observed |
| Live monitoring and broker execution | LEAN supports broker integrations, but this branch has no unattended verified deployment | Algory cloud/local/subscription split is only partially understood |

### Local package inspection (REA, static)

On 2026-10-27, REA inspected the installed Algory 1.5.3.1.8 package without executing it. The application has separate `Algory.exe`, `AlgoryHUD.exe`, and `Engine.exe` files. Static archive inspection identified both `Algory.exe` and `Engine.exe` as PyInstaller one-file packages carrying CPython 3.13. `Engine.exe` is the likely strategy-engine host: its archive contains an `Algory_Engine_v129` entry, a `PYZ.pyz/Algory_Genes_v16.pyc` module, and a compiled `MetaTrader5` package. The smaller `Algory.exe` package instead contains an `Algory_Dashboard_v119` entry plus `Algory_Genes_v16.pyc`, `Algory_UI_Lib_v1.pyc`, and `Algory_Vault_v1.pyc`. REA verified the inspected archive entries against their container integrity metadata.

This narrows the engine location to the Python payload hosted by `Engine.exe`, with related strategy code also bundled in the dashboard package. It does not yet prove which bytecode functions perform breeding, fitness scoring, or backtesting, or whether the GUI launches this engine for a particular workflow. No Algory or engine process was running during this inspection; only the HUD server/window processes were present. The currently available static evidence therefore does not establish runtime call paths, cloud responsibilities, paid entitlements, or function-level implementation. The previous controlled runtime observation recorded MT5 initialization attempts and historical-data requests to an Algory service, but those observations do not prove where all strategy calculations or persistence occur. The installed package also contains a `pyarmor_runtime_011532` support directory; protected modules were not decrypted or bypassed. No third-party binary or code is used by this implementation.

The identified engine host and embedded strategy module are stronger evidence than the prior shell-level identification, but exact Algory genome, genetic operators, fitness formulas, and backtest implementation remain unverified. This project reimplements those behaviors independently and does not evade protections.

The observed application boundary is partial: the desktop UI/HUD and protected engine process run locally; the runtime attempted MetaTrader 5 initialization and fetched historical data from an Algory service. Available evidence does not establish which ranking, storage, portfolio, or monitoring features are local versus server-side, nor which exact capabilities are subscription entitlements. No license bypass is attempted.

## Limitations and remaining work

- Optimizer and LEAN paper processes are coordinated by the supervisor, but supervisor crash recovery and broker-level session recovery have not been integration-tested.
- Walk-forward validation partitions the holdout into contiguous calendar windows and scores the lower median fitness. It does not add purging/embargo gaps or carry open positions between independent LEAN backtests. Parameter probes alter one genome value at a time by fixed increments; this local sensitivity check does not establish broad parameter stability. Monte Carlo diagnostics use closed-trade P&L divided by entry notional and IID bootstrap resampling; randomized significance tests are not implemented.
- Multi-timeframe genes select one resolution per candidate; they do not fuse signals across resolutions.
- Survivorship-bias protection depends on LEAN's installed point-in-time US equity data and corporate-action-aware subscriptions. The available SPY daily archive ends on 2021-03-31, so this smoke run cannot test current market regimes or long-span corporate-action behavior.
- Account slot/exposure calculations include outside-universe brokerage holdings, but cannot attribute positions in the configured symbols to other algorithms or manual orders. Concurrent algorithms can race these checks, so use a dedicated paper account until broker-level coordination is implemented and tested.
- Broker disconnection/rejected-order recovery, supervisor restart recovery, paper-session order outcomes, and concurrent broker-level risk coordination need integration tests before unattended paper deployment.
- Full unattended paper trading requires local market data, a functioning LEAN runtime, Alpaca paper credentials, network access, and an observed session. No profitable performance claim is made.

## Test coverage

The added tests cover deterministic genetic progression, batch completion, scoring differences, genome bounds and configured caps, deterministic parameter neighborhoods, duplicate completion rejection, Monte Carlo repeatability and input validation, lifecycle scheduling/retirement, chronological walk-forward window partitioning/completion, parameter-probe backtest scheduling, existing-position exposure/slot budgets, promotion gates, paper-config guards, bounded paper-process restart backoff, and the LEAN optimizer train/validation contract. The focused `StrategyDiscovery.Tests` suite passes 25/25. Running the full LEAN `Tests` assembly was attempted, but its host aborted during shutdown in the repository Python runtime (`Python.Runtime.Py.GILState.Finalize`: GIL released from a different thread); the focused project avoids loading that unrelated runtime. These tests do not constitute integration coverage for broker execution, market data bias/corporate actions, slippage calibration, process recovery, or live operations.
