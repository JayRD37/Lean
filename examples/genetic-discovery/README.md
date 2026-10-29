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

Paper execution is a separate step. In the Alpaca dashboard, select **Paper Trading** (the dashboard should say no real money is being used), then scroll to **API Keys Endpoint** and choose **Generate New Keys**. Save the paper key and secret privately; never paste them into chat or commit them. The integrated adapter calls Alpaca's paper endpoint and requires a paper key beginning with `PK`.

The direct adapter no longer uses the subscription-validated QuantConnect Alpaca plugin, so QuantConnect credentials are not needed for the broker connection. `start-genetic-paper.ps1` reads Alpaca credentials from the Windows DPAPI-protected file or prompts with hidden input. It passes them to the supervisor, which writes them only to a temporary LEAN config with current-user-only access. The supervisor removes them from the LEAN child's inherited environment.

`Brokerages.AlpacaDirect` now registers as a LEAN brokerage and live market-data queue. It supports US equity market, limit, stop, and stop-limit orders, trade-update events, IEX trade/minute-bar streaming, and account/position reads. It has no live endpoint. The saved account returned `ACTIVE` and unblocked; its IEX market-data stream authenticated successfully without submitting orders. Historical warmup, reconnect recovery, brokerage startup, and an end-to-end paper session still need validation before unattended operation.

Start the local supervisor from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-genetic-paper.ps1
```

To verify the paper key and secret first, run the read-only account check:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-alpaca-paper.ps1
```

It calls only Alpaca's paper `/v2/account` endpoint and submits no orders. If no protected credential file exists, it prompts for the key and secret with hidden input.

To authenticate the paper IEX WebSocket without placing an order, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-alpaca-stream.ps1
```

This authenticates both LEAN's paper brokerage and `AlpacaDirectDataQueueHandler`, then disconnects without submitting an order. Credentials stay out of the console output.

The script reads Alpaca credentials from a Windows DPAPI-protected file scoped to your Windows account, or prompts with hidden input when the file is absent. It passes them to the supervisor process and requires the explicit `--enable-paper` flag. The supervisor has no live-money mode. The encrypted file cannot be decrypted from another Windows account or machine. Before running, review `supervisor.json` and `paper-config.overlay.json`.

Refresh local daily history through 2025 with the paper account's IEX market data:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\update-alpaca-lean-daily.ps1
```

The updater requests raw daily bars for SPY, QQQ, and IWM, merges available Alpaca IEX rows with existing LEAN rows where the IEX feed has no bar, and writes backups under `%LOCALAPPDATA%\AlgoryLean\data-backup`. These market data archives stay local and are not committed. Basic Alpaca data is IEX-only, so this history does not represent consolidated US volume; see [Alpaca's market data plan details](https://docs.alpaca.markets/us/docs/about-market-data-api).

The updater requires at least 2018-2025 coverage after merging before replacing the local archives. A completed backtest or paper session is not evidence of future profitability. See [the integration notes](../../Documentation/GeneticDiscovery.md) for tested behavior and remaining limits.

## What is evolving

Each genome chooses a supported long-only strategy family (trend following, mean reversion, or breakout), a primary timeframe and optional slower confirmation timeframe, signal periods and thresholds, maximum positions, and exposure. Growth and Consistency populations evolve separately; random candidates form a comparison baseline. This is an independently implemented, bounded strategy search, not a reconstruction of Algory's protected internals.







