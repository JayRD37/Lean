# LEAN Discovery Dashboard

A local monitoring interface for the LEAN genetic discovery supervisor, with a guarded emergency-stop action. The interface is independently implemented and uses Algory's documented research workflow as a reference; it does not modify, extend, or load into Algory.

## Run locally

From the repository root, launch:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-genetic-dashboard.ps1
```

Open <http://127.0.0.1:5178>. The server binds only to loopback. Press Ctrl+C in the PowerShell window to close it. If the repository is in another location, set `LEAN_DISCOVERY_ROOT` to the LEAN checkout before running the script.

The dashboard reads `supervisor-output/supervisor-audit.jsonl`, `active-paper-session.json`, and the newest `paper-results-*` LEAN result folder. It polls every five seconds. It does not read or display broker credentials. The Portfolio page reports the latest result file and can lag the brokerage account.

## Controls and safety

There are no start-order or live-trading controls. The Emergency stop button requires a second confirmation and writes the kill-switch path configured in `examples/genetic-discovery/supervisor.json`; the supervisor is responsible for stopping the paper process. It is enabled only when the recorded paper process is running. Live trading remains unavailable through this dashboard.

The dashboard does not independently verify fills or broker state. A running process does not mean market bars have arrived. LEAN's genetic algorithm plots portfolio equity after warmup; the dashboard displays the points LEAN writes to its latest result. Until data events create points, it shows an explicit empty chart.

## Pages

- Command deck: supervisor and LEAN state, active genome, latest result metrics, candidate fitness history, LEAN equity curve, and recent audit events.
- Campaign: optimizer configuration, walk-forward dates, and promotion history from supervisor events.
- Strategy vault: filterable list of candidates that passed the paper promotion gate.
- Portfolio: positions and account value from the latest LEAN result file.
- Insights: recorded validation-window count and fitness, plus data-state caveats.
- Activity: searchable-by-category view of persisted supervisor audit events.

Empty states reflect absent local records. The dashboard does not synthesize performance metrics.
