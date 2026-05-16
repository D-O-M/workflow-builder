# Developer Workflows

## Main Local Run Paths

Run the distributed app through AppHost:

```powershell
dotnet run --project Backend.AppHost/Backend.AppHost.csproj
```

This is the normal way to bring up API, Engine, SignalR, Functions, SQL/Cosmos wiring, observability, and optional seeders together.

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

The Angular build currently passes with an initial bundle budget warning. Treat new build errors as blockers; treat the existing budget warning as known until a bundle-size pass is planned.

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

The PR/manual mocked Playwright workflow is `.github/workflows/ui-playwright.yml`.

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
  -RootDir .\\Backend.Plugins `
  -OutputDir .\\Backend.Plugins\\_bundles `
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
dotnet test Backend.Plugins.Harness.Tests/Backend.Plugins.Harness.Tests.csproj
dotnet test Backend.PluginLoader.Tests/Backend.PluginLoader.Tests.csproj
dotnet test Backend.Engine.Tests/Backend.Engine.Tests.csproj
dotnet build Backend.slnx
```

## Integration and Smoke Entry Points

Main integration suite:

```powershell
./IntegrationTests/scripts/run-integration-tests.ps1
```

AppHost smoke run:

```powershell
./IntegrationTests/scripts/run-apphost-smoke.ps1
```

Performance probe assets and scripts live under `PerformanceTests/`. Use them for targeted non-functional probes rather than replacing the normal unit/build/E2E confidence lanes.

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
