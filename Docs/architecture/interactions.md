# Runtime interactions

These dynamic views follow messages across the containers. Each shows a different starting event so that a Plugin catalog reload, a Step execution and a live notification can be diagnosed independently.

## A caller starts a Process

```mermaid
---
config:
  sequence:
    wrap: true
    width: 140
    actorMargin: 24
---
sequenceDiagram
  actor C as Caller
  participant A as API
  participant S as Cosmos DB
  participant B as Service Bus
  participant E as Engine
  C->>A: POST /api/blueprints/{id}/processes
  A->>A: Resolve latest Blueprint version from SQL
  A->>S: Create Process, pending first Step and dispatch intent
  A->>B: Attempt immediate Step send
  A-->>C: 202 Accepted with Process ID
  B-->>E: Deliver Step
  E->>S: Claim Step and record its result and child intents
```

HTTP acceptance means durable scheduling. Dispatcher drains due intents when an immediate send fails. The first Step can have finished by the time the caller reads the response. A phase start goes through the Phase Series service first, which applies its version policy and supplies initial inputs to the Blueprint start path. [Dispatch guarantees](../agent-context/topology-and-runtime.md#step-dispatch-guarantee) · [First Step code view](engine-execution.md)

## A bundle becomes a runnable Plugin

```mermaid
---
config:
  sequence:
    wrap: true
    width: 140
    actorMargin: 24
---
sequenceDiagram
  participant B as Plugin Blob Storage
  participant D as Plugin Detector
  participant S as SQL task catalog
  participant Q as Reload queue
  participant E as Engine
  B-->>D: Blob trigger or reconciliation finds bundle
  D->>D: Inspect archive, manifest, ABI and schemas
  D->>S: Register task version
  D->>Q: Enqueue reload request
  Q-->>E: Deliver reload request
  E->>S: Read known task versions
  E->>B: Download required bundle
  E->>E: Validate capabilities, prepare config and start agent
```

During local seeding, Plugin Seeder uploads a canonical ZIP if needed and calls the shared bundle processor itself. Detector uses that same processor for uploads and periodic reconciliation. Cosmos records ingestion outcomes and per-Engine runtime status separately. Engine also loads its catalog at startup. A registered version becomes runnable on an Engine once its agent is ready. [Seeder](../../Backend.PluginSeeder/PluginSeedingHostedService.cs) · [Bundle processor](../../Backend.Services/PluginBundleProcessingService.cs) · [Engine catalog load](../../Backend.Engine/PluginRuntimeOrchestrator.cs)

## Process Explorer receives a live update

```mermaid
---
config:
  sequence:
    wrap: true
    width: 140
    actorMargin: 24
---
sequenceDiagram
  participant U as Process Explorer
  participant H as SignalR
  participant C as Cosmos DB
  participant B as Progress Broadcaster
  U->>H: Subscribe to Blueprint and selected Process groups
  C-->>B: Deliver changed Step records
  B->>H: Read active subscription snapshot over HTTP
  H-->>B: Blueprint and Process IDs
  B->>C: Read lightweight projections for affected groups
  B->>H: POST internal/process-events with batch
  H-->>U: processSummaryUpdated for Blueprint subscribers
  H-->>U: processStepsUpdated for Process subscribers
```

Projection Worker consumes the canonical Process and Step change feeds independently. The broadcaster may use a cached subscription snapshot, skips uninterested groups and can rebuild missing projection rows. A successful Process can therefore coexist with an out-of-date browser view. Dashboard uses HTTP summaries. Live log streams use SSE from the API. [Realtime implementation](../../Backend.Functions.ProgressBroadcaster/Services/ProcessProgressBroadcaster.cs) · [End-to-end registration, execution and SignalR tests](../../IntegrationTests/WorkflowBuilder.IntegrationTests/EndToEnd/PluginIngestionAndProcessExecutionTests.cs) · [Operations](../how-to/operations.md)

[Container views](containers.md) · [Architecture index](README.md)
