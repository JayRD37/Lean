# Genetic strategy discovery quickstart

This example combines the independent genetic search module with LEAN's optimizer and isolated backtest runner. The search generates strategy genomes; LEAN runs each candidate; completed LEAN results drive selection, crossover, mutation, held-out validation, and one final confirmation run. No Algory code or binaries are required.

## Run a local search

From the repository root, build LEAN and the genetic adapter:

```powershell
dotnet build QuantConnect.Lean.sln
```

Install the US equity data needed by the selected date ranges, then launch the optimizer:

```powershell
Push-Location Optimizer.Launcher/bin/Debug
dotnet QuantConnect.Optimizer.Launcher.dll --config ../../../examples/genetic-discovery/optimizer-smoke.json
Pop-Location
```

`optimizer-smoke.json` is a small end-to-end search over SPY. It exercises training, validation, parameter probes, and confirmation against the checked-in March 2021 data range. For a larger search over SPY, QQQ, and IWM, use `optimizer.json`; update its date ranges to match the local data you have. Both files use a fixed seed so candidate generation is repeatable.

LEAN's optimizer bounds concurrent backtests with `maximum-concurrent-backtests`. Search history and rankings are written below the configured `results-destination-folder`. Only the final confirmation backtest can produce an optimizer solution; it is later than the training and validation ranges and does not participate in candidate selection.

## Paper deployment

Paper execution is a separate step. Build and stage the pinned Alpaca LEAN plugin with `scripts/setup-alpaca-plugin.ps1`, configure local Alpaca paper credentials outside source control, review `supervisor.json` and `paper-config.overlay.json`, and start the supervisor from `examples/genetic-discovery`. Live trading is disabled by default. A real unattended paper session has not yet been verified; see [the integration notes](../../Documentation/GeneticDiscovery.md) for tested behavior and remaining limits.

## What is evolving

Each genome chooses a supported long-only strategy family (trend following, mean reversion, or breakout), a primary timeframe and optional slower confirmation timeframe, signal periods and thresholds, maximum positions, and exposure. Growth and Consistency populations evolve separately; random candidates form a comparison baseline. This is an independently implemented, bounded strategy search, not a reconstruction of Algory's protected internals.

