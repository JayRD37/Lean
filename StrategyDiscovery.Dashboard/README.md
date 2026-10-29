# LEAN Discovery Dashboard

A local monitoring interface for the LEAN genetic discovery supervisor, with a guarded emergency-stop action. The interface is independently implemented and uses Algory's documented research workflow as a reference; it does not modify, extend, or load into Algory.

## Run locally

From the repository root, launch:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-genetic-dashboard.ps1
```

Open <http://127.0.0.1:5178>. The server binds only to loopback. Press Ctrl+C in the PowerShell window to close it. If the repository is in another location, set `LEAN_DISCOVERY_ROOT` to the LEAN checkout before running the script.

The dashboard reads `supervisor-output/supervisor-audit.jsonl`, `active-paper-session.json`, the newest `paper-results-*` LEAN result folder, and the optimizer results `genetic-search-history.jsonl`. It polls every five seconds. It does not read or display broker credentials. The Portfolio page reports the latest result file and can lag the brokerage account.

## Controls and safety

There are no start-order or live-trading controls. The Emergency stop button requires a second confirmation and writes the kill-switch path configured in `examples/genetic-discovery/supervisor.json`; the supervisor is responsible for stopping the paper process. It is enabled only when the recorded paper process is running. Live trading remains unavailable through this dashboard.

The dashboard does not independently verify fills or broker state. A running process does not mean market bars have arrived. LEAN's genetic algorithm plots portfolio equity after warmup; the dashboard displays the points LEAN writes to its latest result. Until data events create points, it shows an explicit empty chart.

## Pages

- Command deck: supervisor and LEAN state, active genome, latest result metrics, candidate fitness history, LEAN equity curve, and recent audit events.
- Campaign: optimizer configuration, walk-forward dates, and promotion history, plus a per-generation best/median fitness chart, FastPeriod/SlowPeriod genome landscape, population leaders, and recent evaluation records from the optimizer JSONL history.
- Strategy vault: filterable list of candidates that passed the paper promotion gate.
- Portfolio: positions and account value from the latest LEAN result file.
- Insights: recorded validation-window count and fitness, plus data-state caveats.
- Activity: searchable-by-category view of persisted supervisor audit events.

Empty states reflect absent local records. The dashboard does not synthesize performance metrics.


## Algory reference and interface scope

REA inspected the installed Algory executable as a read-only packaged application. The bundle identifies Python 3.13/PyInstaller components including Algory genes, UI library, and vault modules. Matplotlib is present in the package, but that inventory does not establish that Matplotlib drives the Neural Map. No Algory code or binaries are loaded, copied, or reused here.

Algory's public [2.0 interface notes](https://algory.app/g/new-ui.html) describe its Neural Map, candidate fitness curves, and portfolio correlation heatmap; its [workflow guide](https://algory.app/g/algory-how-to-use.html) describes in-sample breeding with held-out out-of-sample evaluation. This dashboard adapts those ideas to data LEAN actually records: per-generation population fitness, a FastPeriod/SlowPeriod genome scatter plot, recent phase-tagged evaluations, and top training candidates. The charts use native SVG in the browser and have no external chart-service dependency.

The current optimizer history does not persist candidate parent/child links, so the landscape does not imply lineage. It also does not provide synchronized per-candidate equity curves or the return series needed to calculate a candidate-correlation heatmap or live trade tape. Those Algory views remain unavailable until LEAN records corresponding data; the dashboard does not fabricate them. REA package inventory is evidence of bundled components, not confirmation of which one renders each Algory view.

