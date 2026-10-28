# Genetic strategy discovery for LEAN

This branch adds an independent, deterministic genetic search module and connects it to LEAN's existing optimizer and backtest runner. The Algory investigation remains behavioral research only; no Algory executable, service, protected code, or license mechanism is used here.

The repository baseline is upstream LEAN commit `22404ea28b8d3f3c9eb74d6ed9f0444426f8e417` (upstream `master` at the fork point, rechecked on 2026-10-27). The exact commit plus the SDK pin makes the build baseline reproducible.

## Architecture

- `StrategyDiscovery/QuantConnect.Genetic.csproj` is the standalone engine. A genome selects long-only trend-following or mean-reversion logic, minute/hour/daily resolution, moving-average and volatility periods, entry/exit thresholds, position count, and gross exposure.
- `Optimizer/Strategies/GeneticSearchOptimizationStrategy.cs` implements LEAN's `IOptimizationStrategy`. It submits candidate parameter sets to LEAN; `ConsoleLeanOptimizer` starts independent LEAN processes and bounds concurrency. Growth and Consistency populations evolve separately. Random candidates provide a baseline.
- `Algorithm.CSharp/GeneticDiscoveryAlgorithm.cs` evaluates one genome using LEAN securities, indicators, portfolio targets, brokerage fee model, slippage, and drawdown risk model. It limits long-only gross exposure, position count, and pairwise return correlation. A file-based emergency stop is checked on data events.
- Training generations are scored on the configured training dates. Finalists from both evolved populations and random candidates are then rerun across configurable, contiguous chronological windows in the disjoint validation date interval. A finalist must finish every window, satisfy constraints in every window, and rank by its lower median window fitness before it can become `Solution`. JSONL evaluation history and ranked training/validation records are written under LEAN's results folder. The ranking file compares evolved finalists with the median held-out random baseline. When LEAN provides closed trades, history also includes a seeded IID bootstrap summary of trade-level returns.
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

`optimizer.json` is a modest search example with three chronological validation windows. `optimizer-smoke.json` reproduces the eight-backtest local pipeline run on the March 2021 SPY data archive with one validation window. Ensure LEAN's matching US equity data is installed before running. Result and LEAN launcher paths are relative to `Optimizer.Launcher/bin/Debug`.

The current interface accepts a non-empty LEAN optimizer `parameters` list for compatibility, while the genetic strategy itself emits genome parameters. The `discovery.marker` static parameter is a required placeholder and is ignored by the algorithm.

## Local end-to-end discovery smoke run

On 2026-10-27, the local `ConsoleLeanOptimizer` completed eight real LEAN backtests from this branch: five training evaluations across Growth, Consistency, and RandomBaseline, followed by three held-out finalist evaluations. All eight produced parseable LEAN statistics; the optimizer wrote `genetic-search-history.jsonl` and `genetic-search-rankings.json`, persisted trade-sequence Monte Carlo diagnostics where closed trades existed, compared validation finalists against the random baseline, and selected its solution from the validation phase. The smoke dates were 2021-01-04 through 2021-03-31 because the checked-out SPY daily data ends on 2021-03-31. This verifies the execution path only; the short sample is not useful evidence of strategy quality.

## Alpaca paper execution

`examples/genetic-discovery/paper-config.overlay.json` documents the LEAN paper brokerage block and parameter names. Merge those values into a private local `config.json`; inject Alpaca paper credentials from a secret manager or environment-specific configuration. Never put credentials in the repository. The base LEAN configuration already sets `alpaca-paper-trading` to `true`, but live execution also requires the algorithm parameter `discovery.allow-paper-trading=true` and an explicit live config selection. These guards prevent accidental live deployment; they are not a substitute for account/broker controls.

Paper trading has not been demonstrated in this workspace: no Alpaca paper credentials were supplied. The example is configuration only, not evidence of a completed unattended paper session.

## Algory comparison

| Capability | This branch | Algory evidence status |
| --- | --- | --- |
| Candidate generation and genetic evolution | Implemented independently: seeded random candidates, tournament selection, elitism, uniform crossover and mutation | Exact Algory genome/operator details remain opaque in its protected runtime |
| Growth and Consistency populations | Separate configurable fitness functions and evolutionary populations | Names and high-level behavior observed/documented; internal scoring is not known |
| Multiple assets and timeframes | US equity symbols and minute/hour/daily bar resolution | Publicly described behavior; detailed processing is unverified |
| Held-out validation | Disjoint date window, finalist and randomized baseline backtests | Validation details beyond observed/documented claims are unverified |
| Backtest engine and brokerage/risk models | LEAN backtester, optimizer, Alpaca brokerage model, risk framework | Independently implemented on LEAN |
| Portfolio-level correlation/exposure limits | Implemented in the example algorithm | Algory internals not observable |
| Chronological walk-forward validation | Configurable contiguous windows; all windows must complete and pass constraints; finalist ranked by the lower median window fitness | Windows are calendar-date partitions, not purged or embargoed; deployment still requires a separately approved process |
| Randomized significance analysis, parameter-neighborhood robustness | Not implemented yet | Documented or commonly expected research functions; runtime details unverified |
| Monte Carlo analysis | Seeded IID bootstrap of observed closed-trade returns with loss probability, terminal-return quantiles, and drawdown quantile | Independent diagnostic; it assumes trade outcomes can be resampled independently and is not a predictive distribution |
| Persistent rankings | Local JSONL history and JSON ranking output | Algory persistence backend is service-dependent and not established |
| Scheduled health reviews and strategy retirement | Implemented in the example with monthly review decisions, drawdown retirement, and retirement after repeated negative periods | Review requests are logged; no external reoptimization/deployment supervisor exists yet |
| Live monitoring and broker execution | LEAN supports broker integrations, but this branch has no unattended verified deployment | Algory cloud/local/subscription split is only partially understood |

The prior investigation found a protected PyInstaller/PyArmor runtime that attempted MetaTrader 5 access and downloaded historical data from Algory's service. UI, external service, entitlement boundaries, and exact engine logic cannot be reliably inferred from the shipped protected binary. This implementation does not evade those protections.

The observed application boundary is partial: the desktop UI/HUD and protected engine process run locally; the runtime attempted MetaTrader 5 initialization and fetched historical data from an Algory service. Available evidence does not establish which ranking, storage, portfolio, or monitoring features are local versus server-side, nor which exact capabilities are subscription entitlements. No license bypass is attempted.

## Limitations and remaining work

- Backtest/optimizer evaluation and a manually configured LEAN paper algorithm are separate processes today. There is no supervisor that promotes an OOS-approved genome, launches scheduled reoptimization, and restarts LEAN with the approved parameters.
- Walk-forward validation partitions the holdout into contiguous calendar windows and scores the lower median fitness. It does not add purging/embargo gaps or carry open positions between independent LEAN backtests. Monte Carlo diagnostics use closed-trade P&L divided by entry notional and IID bootstrap resampling; randomized significance tests and explicit parameter-neighborhood perturbation runs are not implemented.
- Multi-timeframe genes select one resolution per candidate; they do not fuse signals across resolutions.
- Survivorship-bias protection depends on LEAN's installed point-in-time US equity data and corporate-action-aware subscriptions. The available SPY daily archive ends on 2021-03-31, so this smoke run cannot test current market regimes or long-span corporate-action behavior.
- Position and exposure caps apply to this algorithm's selected universe. There is no verified account-wide coordinator for positions opened by other algorithms or manual orders; use a dedicated paper account until that coordinator is implemented and tested.
- A supervisor to act on reoptimization requests and roll approved genomes into a restarted LEAN instance is not implemented. Broker disconnection/rejected-order recovery, process restart/recovery, emergency-stop behavior, and account-wide live risk enforcement need dedicated integration tests before deployment.
- Full unattended paper trading requires local market data, a functioning LEAN runtime, Alpaca paper credentials, network access, and an observed session. No profitable performance claim is made.

## Test coverage

The added tests cover deterministic genetic progression, batch completion, scoring differences, genome bounds, duplicate completion rejection, Monte Carlo repeatability and input validation, lifecycle scheduling/retirement, chronological walk-forward window partitioning and completion, malformed backtest results, and the LEAN optimizer train/validation contract. The focused `StrategyDiscovery.Tests` suite passes 14/14. Running the full LEAN `Tests` assembly was attempted, but its host aborted during shutdown in the repository Python runtime (`Python.Runtime.Py.GILState.Finalize`: GIL released from a different thread); the focused project avoids loading that unrelated runtime. These tests do not constitute integration coverage for broker execution, market data bias/corporate actions, slippage calibration, process recovery, or live operations.
