# WorkflowBuilder NFT Proof

This folder contains non-functional tests for WorkflowBuilder, using either a local AppHost or an existing Azure deployment.

## Run

```powershell
PerformanceTests/scripts/run-local-nft.ps1 -Profile Smoke
PerformanceTests/scripts/run-local-nft.ps1 -Profile Standard
PerformanceTests/scripts/run-local-nft.ps1 -Profile Staircase
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard
```

The runner starts AppHost detached, seeds performance-only plugin bundles and blueprints, runs bounded load scenarios, writes artifacts under `.artifacts/nft/<timestamp>/`, and stops AppHost unless `-KeepAppHost` is supplied.

## Run Against Azure

Remote mode uses the local machine only as the load injector. It does not start AppHost or any local WorkflowBuilder service, and it does not collect local process/Docker counters. It builds Linux plugin bundles, uploads missing bundles to Azure Storage, imports the NFT blueprints through the deployed API, performs a completed-process warm-up, runs the normal scenario matrix, and captures Azure Monitor metrics.

```powershell
PerformanceTests/scripts/run-local-nft.ps1 `
  -Target Remote `
  -ApiBaseUrl https://<api-container-app> `
  -AzureResourceGroup <resource-group> `
  -AzureStorageAccountName <storage-account> `
  -Profile Smoke `
  -ScenarioKeys one-node,fanout-10,collection-iterator,bulk-start `
  -GateMode Warn
```

When the target requires bearer authentication, set `WORKFLOWBUILDER_NFT_ACCESS_TOKEN` in the injector environment, or pass an already-loaded variable with `-AccessToken $token`. The runner passes it to the probe and k6 through scoped environment variables; token values are not included in child-process command arguments or run metadata. The probe also reads `WORKFLOWBUILDER_NFT_ACCESS_TOKEN` when invoked directly. Use a token valid for the duration of the run; the harness does not refresh it.

Use `-SkipAssetSeeding` only after the NFT task definitions and blueprints have already been verified in that environment. In remote mode this also skips bundle building. Remote bundles default to `linux-x64`; override `-PluginRuntimeIdentifier` only when the deployed Engine uses another runtime. Product lanes select only bundles whose metadata matches that runtime; use `-BuildProductBundles` if matching bundles are missing.

Copy `Infra/Terraform/environments/demo/nft.tfvars.example` to ignored `nft.tfvars`, set the image tag under test, and apply that copy as a second var file before a comparable run. Follow the demo README for updating existing Container App images. This profile keeps API, Engine, Dispatcher, and ProjectionWorker warm with fixed replica counts. Reapply only the normal demo tfvars afterward to return to the cheap scale-to-zero profile.

## Throughput Scoreboard

The scoreboard profile generates deterministic performance blueprints from `PerformanceTests/scoreboard-scenarios.json`, runs the selected scenario matrix, and writes comparable artifacts. Local mode starts AppHost once; remote mode uses the deployed target.

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
- Remote mode writes `azure-deployment.json` and `azure-metrics.json` instead of attributing injector counters to the deployed services.
