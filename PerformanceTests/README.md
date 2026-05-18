# WorkflowBuilder Local NFT Proof

This folder contains the local non-functional testing setup for proving WorkflowBuilder performance on a developer machine.

## Run

```powershell
PerformanceTests/scripts/run-local-nft.ps1 -Profile Smoke
PerformanceTests/scripts/run-local-nft.ps1 -Profile Standard
PerformanceTests/scripts/run-local-nft.ps1 -Profile Staircase
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard
```

The runner starts AppHost detached, seeds performance-only plugin bundles and blueprints, runs bounded load scenarios, writes artifacts under `.artifacts/nft/<timestamp>/`, and stops AppHost unless `-KeepAppHost` is supplied.

## Throughput Scoreboard

The scoreboard profile starts AppHost once, generates deterministic performance blueprints from `PerformanceTests/scoreboard-scenarios.json`, runs the selected scenario matrix, and writes comparable artifacts:

- `throughput-scoreboard.json`: machine-readable run metadata, scenario configs, metrics, gates, and baseline deltas.
- `throughput-scoreboard.md`: human-readable matrix for throughput, latency, runtime counters, RU/process, and RU/step.
- `scenario-windows.json`: UTC start/end windows used to slice counter CSVs.
- `scenarios/<scenarioKey>/`: per-scenario probe CSVs and k6 summaries.

Useful scoreboard commands:

```powershell
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard -GateMode Warn
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard -ScenarioKeys one-node,fanout-10,bulk-start
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard -BaselinePath .artifacts/nft/<baseline>/throughput-scoreboard.json -GateMode Fail
```

`GateMode` values are `Off`, `Warn`, and `Fail`. `Warn` records threshold/baseline breaches without failing the run; `Fail` exits non-zero after writing artifacts. The disabled `native-light` scenario is present in the manifest as a placeholder until a native operator beyond Collection Iterator exists.

## Lanes

- `ceiling-lane`: deterministic local plugins and the `nft-ceiling-fanout-join` blueprint, avoiding public API noise.
- `product-lane`: existing product seed blueprints and production bundles.
- `both`: stages both sets of blueprints and bundles in one local run.

## Tools

- Uses local `k6` when installed.
- Falls back to the free `grafana/k6` Docker image when local `k6` is missing.
- Installs `dotnet-counters` as a global .NET tool if missing, then captures API and Engine runtime counters when process discovery succeeds.
