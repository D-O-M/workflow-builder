# Integration Tests

This folder contains the backend integration test workflows for the workflow runtime.

## Default lane

`WorkflowBuilder.IntegrationTests/` is the primary integration suite.

It uses:
- real API and real engine hosted in-process
- deterministic integration-test plugin bundles from `Plugins/`
- direct plugin registration through `IPluginBundleProcessingService`
- SQLite for relational persistence
- in-memory workflow/task state repositories mirrored to files for plugin-owned hydration
- an in-memory queue that preserves the engine queue contract

This lane is the one we expect developers and CI to run by default.

Scenarios currently covered:
- plugin registration happy path
- duplicate bundle idempotency
- blueprint authoring + versioning
- end-to-end workflow happy path
- end-to-end workflow failure path
- in-memory queue abandon/renew/complete behavior

Run it locally with:

```powershell
./IntegrationTests/scripts/run-integration-tests.ps1
```

Run it in a detached process with live log files:

```powershell
./IntegrationTests/scripts/run-integration-tests.ps1 -Detached
```

Use `-NoBuild` when you already have a fresh build and want a quicker rerun:

```powershell
./IntegrationTests/scripts/run-integration-tests.ps1 -NoBuild
```

## Manual smoke lane

`WorkflowBuilder.IntegrationTests.AppHost/` is kept only as a manual AppHost/emulator smoke path.

It is not the default integration lane and it is not required for normal CI or day-to-day development. Use it only when you want a quick confidence check that the external Aspire wiring still boots.

Run the manual smoke lane with:

```powershell
./IntegrationTests/scripts/run-apphost-smoke.ps1
```

This smoke path expects local AppHost-compatible configuration and Docker/emulator support.
