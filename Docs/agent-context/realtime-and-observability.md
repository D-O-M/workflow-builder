# Realtime and Observability

## Realtime Is a Product Requirement

Live process updates are part of the expected application behavior.

The process explorer depends on them to reflect:

- process status changes
- step status transitions
- live progress while a process is running

If execution succeeds but the UI stays stale, treat that as a real product issue, not cosmetic polish.

## Required Live-Update Path

1. The API creates or updates process and process-step records.
2. The engine updates Cosmos `processsteps` documents as execution advances.
3. `ProgressBroadcasterFunction` listens to the Cosmos `processsteps` container.
4. The broadcaster builds `ProcessRealtimeBatch` payloads from changed steps.
5. The broadcaster publishes to `Backend.SignalR`.
6. The frontend connects to `/hubs/processes`.
7. `process-realtime.service.ts` subscribes to blueprint/process groups.
8. The UI receives:
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
  - projects changed steps into realtime batches
- `Backend.SignalR/Hubs/ProcessProgressHub.cs`
  - subscription groups by blueprint and process
- `UI/src/app/domain/process-realtime.service.ts`
  - connects using runtime client-config
  - subscribes and deduplicates incoming events

## Known Brittle Points

### Broadcaster binding config

The function depends on the Cosmos trigger binding resolving correctly at startup. If the broadcaster is disabled or never indexes cleanly, execution can still succeed while live updates fail.

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

## Debugging Frame

When diagnosing a process-progress problem, classify it as one of:

- execution only
  - process itself is failing before state advances
- broadcaster only
  - execution succeeds, Cosmos changes exist, but realtime events are not published
- SignalR or client-config only
  - broadcaster publishes, but the browser is not subscribed correctly
- end-to-end realtime path breakage
  - multiple links are miswired

Start narrow:

1. verify the process is actually advancing in backend state
2. verify the broadcaster is running and bound
3. verify SignalR subscriptions are live
4. verify the UI is listening for the correct event names
