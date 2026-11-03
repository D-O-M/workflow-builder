# Realtime and Observability

## Realtime Is a Product Requirement

Live process updates are part of the expected application behavior.

The process explorer depends on them to reflect:

- process status changes
- step status transitions
- live progress while a process is running
- phased process progression
- native Collection Iterator step progression

If execution succeeds but the UI stays stale, treat that as a real product issue, not cosmetic polish.

## Required Live-Update Path

1. The API creates or updates process and process-step records.
2. The engine updates Cosmos `processsteps` documents as execution advances.
3. `Backend.ProjectionWorker` maintains lightweight realtime projections from canonical process/process-step changes. Engine synchronous projection writes may also run only when `Projections:SynchronousEngineUpdatesEnabled=true`.
4. `ProgressBroadcasterFunction` listens to the Cosmos `processsteps` container.
5. The broadcaster builds `ProcessRealtimeBatch` payloads from lightweight realtime projections.
6. The broadcaster publishes to `Backend.SignalR`.
7. The frontend connects to `/hubs/processes`.
8. `process-realtime.service.ts` subscribes to blueprint/process groups.
9. The UI receives:
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
  - projects changed steps into realtime batches from `processrealtimesummaries` and `processnoderealtime`
  - falls back to source process/process-step loading only to repair missing projections
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

The function depends on the Cosmos trigger binding resolving correctly at startup. If the broadcaster is disabled or never indexes cleanly, execution can still succeed while live updates fail.

### Projection worker health

`Backend.ProjectionWorker` is the async owner for projection maintenance. If it is stopped while `Projections:SynchronousEngineUpdatesEnabled=false`, canonical process execution can keep moving but dashboard, process summary, and live graph data may lag until the worker catches up. The worker requires the `processprojectionleases` container.

ProjectionWorker emits:

- `projection_worker_processed_items_total`
- `projection_worker_failures_total`
- `projection_worker_lag_ms`

Engine emits `engine_synchronous_projection_updates_enabled`. In production/performance mode this should report `0`, with ProjectionWorker lag staying bounded.

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

The frontend uses runtime client-config to discover the SignalR base URL. If client-config points at the wrong host or port, subscriptions fail even if the hub is healthy.

### Dashboard and Process Explorer dependence

The dashboard’s recent/runtime views and the process explorer’s live graph behavior both depend on the realtime path. Regressions here often surface as “UI stale” symptoms rather than obvious backend failures.

### Phases dependence

Phased blueprint runtime should not require a separate realtime channel. Phase progression and Data Bridge handoffs must remain observable through the normal process and process-step update path.

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
