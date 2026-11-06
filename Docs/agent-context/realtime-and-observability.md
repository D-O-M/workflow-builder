# Realtime and Observability

Process Explorer overlays live Process and Step updates on its HTTP data. Dashboard reads aggregate HTTP endpoints. The Process and Step log panes read Loki through the API. Diagnose each surface along its own path. [Runtime interactions](../architecture/interactions.md) · [Operations runbooks](../how-to/operations.md)

## Required Live-Update Path

1. The API creates or updates process and process-step records.
2. The engine updates Cosmos `processsteps` documents as execution advances.
3. `Backend.ProjectionWorker` maintains lightweight realtime projections from canonical process/process-step changes. Engine synchronous projection writes may also run only when `Projections:SynchronousEngineUpdatesEnabled=true`.
4. Process Explorer connects to `/hubs/processes` and subscribes to the selected Blueprint and Process groups.
5. The broadcaster observes Cosmos `processsteps` changes and fetches an active-subscription snapshot from SignalR. The Function trigger is defined in every deployment. `Realtime:UseHostedChangeFeed=true` additionally registers a hosted change-feed processor in the worker. Both paths call the same broadcaster. Integration mode and the Azure demo set the flag.
6. For subscribed identifiers, the broadcaster builds `ProcessRealtimeBatch` payloads from projections and posts them to the SignalR service over HTTP. SignalR delivers the corresponding events to the subscribed groups.
7. The UI handles:
   - `processSummaryUpdated`
   - `processStepsUpdated`

## What “Working Realtime” Means

For a healthy end-to-end path:

- the broadcaster function starts successfully
- SignalR subscriptions can be established
- process and step updates appear while the process is still running
- the UI does not require a manual refresh to observe normal process progress

## Main Components

- `Backend.Functions.ProgressBroadcaster/ProgressBroadcasterFunction.cs`
  - Cosmos trigger over `processsteps`
- [ProcessProgressBroadcaster](../../Backend.Functions.ProgressBroadcaster/Services/ProcessProgressBroadcaster.cs) and [projection service](../../Backend.Repositories/Processes/ProcessProjectionService.cs)
  - select active subscriptions and build batches from `processrealtimesummaries` and `processnoderealtime`
  - fall back to canonical Process/Step state when projections are missing, repairing projections when available
- `Backend.ProjectionWorker`
  - Cosmos change feed over `processes` and `processsteps`
  - updates `processdirectory`, `processsummaries`, `processrealtimesummaries`, and `processnoderealtime`
  - stores change-feed leases in `processprojectionleases`
- `Backend.SignalR/Hubs/ProcessProgressHub.cs`
  - subscription groups by blueprint and process
- `UI/src/app/domain/process-realtime.service.ts`
  - connects using runtime client-config
  - subscribes and deduplicates incoming events
- `Backend.Engine` and `Backend.Plugins.Harness`
  - engine-created process-step spans pass W3C trace context through `InvocationEnvelope`
  - plugin harness logs include the same `process.id` and `process.step_id` labels used by engine logs
  - plugin telemetry exports through the OTLP endpoint inherited from the engine/AppHost, with no plugin-author setup
- `Backend.Engine/CollectionIteratorExecutionService.cs`
  - native operator execution still writes process-step state into the same realtime path
- `Backend.Repositories/PhasedProcessRepository.cs`
  - phased process projection and finalization still flow through process/process-step state observed by the UI

## Known Brittle Points

### Local observability services

The normal AppHost `http` profile enables Grafana, Loki, Tempo, Prometheus, and Alloy. The `nft` profile disables them intentionally. When `Observability:Enabled=false`, AppHost disables telemetry export and supplies unreachable observability URLs, so the UI cannot load logs or open Grafana. Restart AppHost after changing this setting and reload the frontend to refresh its runtime client configuration. Logs produced while telemetry export was disabled are not backfilled.

Process, step, and blueprint identifiers arrive in Loki as OTLP structured metadata. Query them with a service selector followed by metadata filters, such as `{service_name=~".+"} | process_id="..."`. Live log streams authenticate with bearer headers and resume from the last received cursor when reconnecting.

### Broadcaster binding config

The Functions host resolves the `RealtimeCosmos` binding and the database, Step container and lease container settings before the worker can handle trigger deliveries. For the additional hosted reader, look for its startup message and failures reading the change feed. If the publisher fails to fetch subscriptions or SignalR rejects a batch, inspect the gateway's warnings and publisher credentials. [Gateway](../../Backend.Functions.ProgressBroadcaster/Services/SignalRProcessGateway.cs) · [Publisher access](../authentication-keycloak.md#api-and-signalr)

### Projection worker health

`Backend.ProjectionWorker` is the async owner for projection maintenance. If it is stopped while `Projections:SynchronousEngineUpdatesEnabled=false`, canonical process execution can keep moving but dashboard, process summary, and live graph data may lag until the worker catches up. The worker requires the `processprojectionleases` container.

ProjectionWorker emits:

- `projection_worker_processed_items_total`
- `projection_worker_failures_total`
- `projection_worker_lag_ms`

The Engine emits `engine_synchronous_projection_updates_enabled`. Compare this value to the environment under investigation. The Azure demo example uses synchronous Engine updates and omits ProjectionWorker. The Azure NFT override enables ProjectionWorker and switches the Engine flag off. [Deployment profiles](../architecture/deployment.md)

### Plugin agent health

`Backend.Engine` publishes structured plugin-agent health metrics for runtime failures and suspicious protocol behavior:

- `plugin_agent_health_events_total`
- `plugin_agent_restarts_total`
- `plugin_agent_quarantined_agents_total`
- `plugin_agent_quarantined_task_versions_total`
- `plugin_agent_repeated_crashes_total`

Task-version quarantine from this policy is runtime-only for the current Engine process. It appears in task catalog runtime availability as `Quarantined`, while bundle ingestion quarantine remains owned by PluginDetector/admin flows.

### Process-step queue naming

`process-step-queue` resource naming and `processsteps` entity naming need to stay aligned across:

- AppHost
- Engine
- integration harness
- smoke scripts

Queue mismatches break execution before realtime ever has a chance to work.

### SignalR base URL and client-config wiring

The frontend loads same-origin `/runtime-bootstrap.json`, then fetches `/api/client-config` from the resulting API address. Inspect the returned hub URL and the browser's connection and subscription status. [Client bootstrap](../../UI/src/app/domain/runtime-client-config.service.ts)

### Dashboard and Process Explorer dependence

Dashboard queries the observability HTTP API on page load and when the selected Blueprint or time window changes. Process Explorer loads Process detail over HTTP and overlays SignalR updates. Logs use HTTP queries and authenticated server-sent event streams through the API's Loki client. The Terraform demo supplies no Loki service. [Dashboard queries](../../UI/src/app/pages/dashboard-page.component.ts) · [Process Explorer subscriptions](../../UI/src/app/pages/process-explorer-page.component.ts) · [Log endpoints](../../Backend.API/Controllers/ObservabilityController.cs)

### Phases dependence

Process Explorer loads the enclosing phased Process over HTTP on selection or refresh. Each selected child Process uses the same Process and Step subscriptions as a standalone Process.

## Debugging Frame

When diagnosing a process-progress problem, classify it as one of:

- execution only
  - process itself is failing before state advances
- broadcaster only
  - execution succeeds, Cosmos changes exist, but realtime events are not published
- projection worker only
  - execution succeeds and broadcaster is running, but lightweight projection records lag or are missing
- SignalR or client-config only
  - broadcaster publishes, but the browser is not subscribed correctly
- end-to-end realtime path breakage
  - multiple links are miswired

Start narrow:

1. verify the process is actually advancing in backend state
2. verify ProjectionWorker is running or synchronous engine projection updates are enabled
3. verify the broadcaster is running and bound
4. verify SignalR subscriptions are live
5. verify the UI is listening for the correct event names
