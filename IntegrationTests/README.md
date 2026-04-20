# Integration Tests

This folder contains the backend integration tests for the blueprint and process runtime.

## Default lane

`WorkflowBuilder.IntegrationTests/` is the primary integration suite.

It uses:
- the real `Backend.AppHost` distributed topology through `Aspire.Hosting.Testing`
- deterministic integration-test plugin bundles from `Plugins/`
- blob upload through the real ingestion pipeline
- real SQL, Cosmos, storage, Service Bus, SignalR, and function wiring from the main AppHost

This lane is the one we expect developers and CI to run by default.

Scenarios currently covered:
- plugin registration happy path
- duplicate bundle idempotency
- blueprint authoring + versioning
- end-to-end process happy path
- end-to-end process failure path
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

## Legacy smoke lane

`WorkflowBuilder.IntegrationTests.AppHost/` is a legacy manual smoke path and is no longer the primary integration harness.

The real `Backend.AppHost` is now the source of truth for distributed test topology.
