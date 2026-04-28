# WorkflowBuilder Local NFT Proof

This folder contains the local non-functional testing setup for proving WorkflowBuilder performance on a developer machine.

## Run

```powershell
PerformanceTests/scripts/run-local-nft.ps1 -Profile Smoke
PerformanceTests/scripts/run-local-nft.ps1 -Profile Standard
PerformanceTests/scripts/run-local-nft.ps1 -Profile Staircase
```

The runner starts AppHost detached, seeds performance-only plugin bundles and blueprints, runs bounded load scenarios, writes artifacts under `.artifacts/nft/<timestamp>/`, and stops AppHost unless `-KeepAppHost` is supplied.

## Lanes

- `ceiling-lane`: deterministic local plugins and the `nft-ceiling-fanout-join` blueprint, avoiding public API noise.
- `product-lane`: existing product seed blueprints and production bundles.
- `both`: stages both sets of blueprints and bundles in one local run.

## Tools

- Uses local `k6` when installed.
- Falls back to the free `grafana/k6` Docker image when local `k6` is missing.
- Installs `dotnet-counters` as a global .NET tool if missing, then captures API and Engine runtime counters when process discovery succeeds.
