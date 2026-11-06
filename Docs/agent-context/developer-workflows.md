# Developer Workflows

## Main Local Run Paths

For a first checkout, follow the [first Process tutorial](../tutorials/first-process.md) for prerequisites, a small local Plugin bundle and an expected result. When starting the full showcase catalog, build the canonical bundles below for the Engine's host runtime first. A fresh checkout has no `_bundles` directory and the seeder skips a missing directory.

Start the backend from the repository root:

```powershell
dotnet run --project Backend.AppHost/Backend.AppHost.csproj --launch-profile http
```

Use the Aspire dashboard URL printed at startup to inspect migrations, platform services and optional seeders. The checked-in `http` launch profile sets `Frontend__Enabled=false`. In a second terminal, run `npm ci` and `npm start` from `UI`, then open the displayed Angular address. Alternatively, add `-- --Frontend:Enabled=true` to the AppHost command to host Angular in Docker. [Deployment views](../architecture/deployment.md) · [Local sign-in](../authentication-keycloak.md)

For Visual Studio debugging, set `Backend.AppHost` as the startup project and press F5. Both Functions projects explicitly declare the `AzureFunctions` project capability and `<FunctionsExecutionModel>isolated</FunctionsExecutionModel>` so Visual Studio starts the Functions host and attaches to its worker. Keep these declarations when updating the Functions SDK. The host supplies `Functions:Worker:HostEndpoint` dynamically; launching the worker executable directly does not provide it.

## Targeted Builds

Backend API:

```powershell
dotnet build Backend.API/Backend.API.csproj
```

Engine:

```powershell
dotnet build Backend.Engine/Backend.Engine.csproj
```

Engine capabilities:

```powershell
dotnet build Backend.Engine.Capabilities/Backend.Engine.Capabilities.csproj
```

SignalR:

```powershell
dotnet build Backend.SignalR/Backend.SignalR.csproj
```

Progress Broadcaster:

```powershell
dotnet build Backend.Functions.ProgressBroadcaster/Backend.Functions.ProgressBroadcaster.csproj
```

Frontend:

```powershell
Set-Location UI
npm run test:unit
npm run build
```

Check the command's exit code and build output for the current bundle budget result.

## Frontend Playwright

The frontend-first Playwright suite lives under `UI/e2e/`.

Default mocked confidence lane:

```powershell
Set-Location UI
npm run test:e2e
```

Equivalent mocked lane alias:

```powershell
Set-Location UI
npm run test:e2e:mocked
```

Interactive Playwright UI:

```powershell
Set-Location UI
npm run test:e2e:ui
```

Snapshot update lane:

```powershell
Set-Location UI
npm run test:e2e:update-snapshots
```

Runtime smoke assumes AppHost is already running and uses the real API through the dev proxy:

```powershell
Set-Location UI
npm run test:e2e:runtime
```

Playwright artifacts are ignored:

- `UI/playwright-report/`
- `UI/test-results/`

The [mocked Playwright workflow](../../.github/workflows/ui-playwright.yml) runs on UI pull requests and can be started manually. On a clean development machine, install the suite's browsers from `UI` with `npx playwright install chromium firefox webkit`.

## Frontend Test Conventions

- Use maintained `data-testid` attributes for broad E2E flows.
- Use the passive `?e2e=1` canvas inspection seam for reliable canvas pointer interactions.
- Keep the seam readonly; do not add test-only mutation behavior.
- Page objects live in `UI/e2e/pages`.
- Mocked API state lives in `UI/e2e/support/mock-workflow-api.ts` and fixture data in `UI/e2e/fixtures`.
- Runtime smoke should stay separate from PR gating because it depends on distributed AppHost health.

## Rebuild Canonical Plugin Bundles

Use the canonical bundle script, not ad hoc publish-and-zip logic:

```powershell
pwsh -File BuildScripts/bundle-plugins.ps1 `
  -RootDir Backend.Plugins `
  -OutputDir Backend.Plugins/_bundles `
  -Configuration Release `
  -RuntimeIdentifier win-x64 `
  -Overwrite
```

Use a runtime identifier appropriate for the local execution environment. The integration helper script derives the current RID automatically.
The canonical plugin publish shape is a framework-dependent apphost bundle; only opt into self-contained or single-file bundles for a specific portability requirement.
The script refreshes plugin `manifest.json` and schema artifacts from the generated design-time contract output under `obj/<Configuration>/<TFM>/plugin-contract` before packaging.

Integration helper:

```powershell
pwsh -File IntegrationTests/scripts/build-integration-plugin-bundles.ps1
```

## Capability and Plugin Config Checks

Use these when changing plugin capabilities, config contracts, harness binding, manifest generation, or runtime capability gating:

```powershell
dotnet test --project Backend.Plugins.Harness.Tests/Backend.Plugins.Harness.Tests.csproj
dotnet test --project Backend.PluginLoader.Tests/Backend.PluginLoader.Tests.csproj
dotnet test --project Backend.Engine.Tests/Backend.Engine.Tests.csproj
dotnet build Backend.slnx
```

## Integration and Smoke Entry Points

The [integration workflow](../../.github/workflows/integration-tests.yml) is started manually. Its runner stops stale local AppHost and integration-test processes before testing. Use a separate session from an interactive runtime smoke check. [Integration scenarios](../../IntegrationTests/README.md)

Main integration suite:

```powershell
./IntegrationTests/scripts/run-integration-tests.ps1
```

Legacy manual smoke using the separate integration AppHost:

```powershell
./IntegrationTests/scripts/run-apphost-smoke.ps1
```

Performance probe assets and scripts live under `PerformanceTests/`. Use them for targeted non-functional probes rather than replacing the normal unit/build/E2E confidence lanes.

Throughput scoreboard:

```powershell
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard -GateMode Warn
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard -ScenarioKeys one-node,fanout-10,collection-iterator,bulk-start
PerformanceTests/scripts/run-local-nft.ps1 -Profile Scoreboard -BaselinePath .artifacts/nft/<baseline>/throughput-scoreboard.json -GateMode Fail
```

Azure target smoke run (the local machine is only the injector):

```powershell
PerformanceTests/scripts/run-local-nft.ps1 -Target Remote `
  -ApiBaseUrl https://<api-container-app> `
  -AzureResourceGroup <resource-group> `
  -AzureStorageAccountName <storage-account> `
  -Profile Smoke `
  -ScenarioKeys one-node,fanout-10,collection-iterator,bulk-start
```

The scoreboard writes `throughput-scoreboard.json`, `throughput-scoreboard.md`, `scenario-windows.json`, and per-scenario artifacts under `.artifacts/nft/<timestamp>/scenarios/<scenarioKey>/`. It uses deterministic runtime-generated blueprints for large linear/fan-out graphs and reports explicit `n/a` values when optional realtime, Prometheus, or counter data is unavailable.

## Practical Rules

- AppHost is the source of truth for distributed wiring.
- Use targeted builds when iterating on one subsystem, but use AppHost when validating end-to-end behavior.
- Rebuild plugin bundles after harness or communication-contract changes.
- Rebuild plugin manifests/bundles after capability, configuration-contract, or manifest-generator changes.
- Treat realtime regressions as runtime correctness issues, not just observability issues.
- For UI work, run `npm run test:unit`, `npm run build`, and the relevant Playwright lane before committing.
- For Phases/canvas work, include mocked Playwright Phases coverage when behavior or performance can be user-visible.

## Local-Only and Ignored Outputs

These are intentionally not committed:

- `.artifacts/` and nested `**/.artifacts/`
- `.github/skills/`
- `IntegrationTests/WorkflowBuilder.IntegrationTests/LiveRuns/`
- `UI/Codex Installer.exe`
- `UI/Microsoft.Services.Store.winmd`
- `Backend.Plugins/_bundles/`

## Common Gotchas

- stale plugin bundles can break execution even when plugin source code is unchanged
- missing required capabilities make plugin versions unrunnable; check engine capability validation before debugging plugin launch
- the progress broadcaster can fail independently of process execution
- AppHost/client-config mismatches can make SignalR look broken from the UI side
- `_bundles` output is disposable and is meant to be reseeded, not treated as source
- stale Playwright snapshots usually indicate either intentional UI change or a real layout regression; inspect before updating
