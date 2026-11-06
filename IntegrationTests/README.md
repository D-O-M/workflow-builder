# Integration Tests

This folder contains the backend integration tests for the blueprint and process runtime.

## Default lane

`WorkflowBuilder.IntegrationTests/` is the primary integration suite.

It uses:
- the real `Backend.AppHost` distributed topology through `Aspire.Hosting.Testing`
- deterministic integration-test plugin bundles from `Plugins/`
- blob upload through the real ingestion pipeline
- real SQL, Cosmos, storage, Service Bus, SignalR, and function wiring from the main AppHost

Use this suite when changes cross service boundaries. The [GitHub integration workflow](../.github/workflows/integration-tests.yml) runs on manual dispatch. The [UI Playwright workflow](../.github/workflows/ui-playwright.yml) runs the mocked browser suite on UI pull requests.

Scenarios currently covered:
- plugin registration happy path
- duplicate bundle idempotency
- blueprint authoring + versioning
- end-to-end process happy path
- end-to-end process failure path
- SignalR updates for a selected Blueprint and Process
- fan-out, joins, partial data transfer and failure on an inbound branch
- precedence of design-time and runtime input overrides
- in-memory queue abandon/renew/complete behavior

Run it locally with:

```powershell
./IntegrationTests/scripts/run-integration-tests.ps1
```

Use a machine with a working local AppHost environment. The runner stops stale AppHost and integration-test processes before starting a shared fixture, builds the project unless `-NoBuild` is set, and runs it using Microsoft.Testing.Platform. Run interactive UI smoke checks separately. [Developer workflows](../Docs/agent-context/developer-workflows.md)

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
