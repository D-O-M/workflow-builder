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
npm run build
npm run test:unit
```

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
The script refreshes plugin `manifest.json` and schema artifacts by running each plugin once in Debug contract-generation mode before the Release publish step.

Integration helper:

```powershell
pwsh -File IntegrationTests/scripts/build-integration-plugin-bundles.ps1
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

## Practical Rules

- AppHost is the source of truth for distributed wiring.
- Use targeted builds when iterating on one subsystem, but use AppHost when validating end-to-end behavior.
- Rebuild plugin bundles after harness or communication-contract changes.
- Treat realtime regressions as runtime correctness issues, not just observability issues.

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
- the progress broadcaster can fail independently of process execution
- AppHost/client-config mismatches can make SignalR look broken from the UI side
- `_bundles` output is disposable and is meant to be reseeded, not treated as source
