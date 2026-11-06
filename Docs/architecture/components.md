# Components · WorkflowBuilder

**C4 Level 3.** Each boundary below encloses components in one application. Nodes outside a boundary are neighboring containers. Start with the API to understand version selection, the Engine to follow Step execution, or the Progress Broadcaster to trace a live update.

## API

```mermaid
flowchart TB
  client["Browser or API client"]
  subgraph api["Backend.API · ASP.NET Core"]
    routes["Blueprint and Phase Series controllers"]
    phase["PhasedBlueprintProcessService<br/>Resolves phase policy, correlation and inputs"]
    start["BlueprintProcessService<br/>Creates Process, first Step and dispatch intent"]
    queries["ProcessQueryService<br/>Loads history, detail and Step data"]
    read["Process query controllers"]
    routes -->|Starts Blueprint by definition ID| start
    routes -->|Starts series or handles a trigger| phase
    phase -->|Starts selected Blueprint version| start
    read -->|Requests query results| queries
  end
  client -->|Submits HTTP commands| routes
  client -->|Reads Process details over HTTP| read
  start -->|Reads executable Blueprint| sql[("SQL Server")]
  start -->|Persists initial state and intent| cosmos[("Cosmos DB")]
  queries -->|Reads runtime records and projections| cosmos
```

Blueprint start and phase handoff share the same start service. The API can attempt a Service Bus send before returning acceptance. Other controller families serve the task catalog, observability and Plugin ingestion administration. [Blueprint start](../../Backend.API/Services/BlueprintProcessService.cs) · [Phase start and trigger](../../Backend.API/Services/PhasedBlueprintProcessService.cs) · [Query service](../../Backend.API/Services/ProcessQueryService.cs)

## Engine

```mermaid
---
config:
  flowchart:
    nodeSpacing: 20
    rankSpacing: 34
    padding: 8
    wrappingWidth: 145
---
flowchart TB
  bus[["Service Bus · processsteps"]]
  subgraph eng["Backend.Engine · .NET service"]
    ingress["QueueConsumer<br/>StepMessageHandler"]
    exec["StepExecutionService<br/>StepClaimService"]
    iter["CollectionIteratorExecutionService<br/>Runs the native collection operator"]
    runtime["PluginRuntimeOrchestrator<br/>AgentManager"]
    agent["PluginRuntimeAgent<br/>Child-process protocol"]
    final["StepFinalizationService<br/>Persist outcome or resume"]
    ingress -->|Routes a Plugin Step| exec
    ingress -->|Routes an iterator| iter
    ingress -->|Resumes a persisted result| final
    exec -->|Obtains an available agent| runtime
    runtime -->|Initializes and registers| agent
    agent -->|Queues result for finalization| final
    iter -->|Commits result and fan-out| final
  end
  bus -->|Delivers work| ingress
  agent -->|Exchanges job and result frames| child["Plugin executable · separate process"]
  final -->|Writes outbox and schedules downstream Steps| cosmos[("Cosmos DB")]
```

Plugin jobs go to running agents. Startup and reload perform ABI, capability and configuration checks. During a Plugin job, Engine renews the Service Bus lock. A bounded result queue feeds workers which call the execution service and then the finalizer. Engine can also execute a Collection Iterator entirely in-process. [Message routing](../../Backend.Engine/StepMessageHandler.cs) · [Agent lifecycle](../../Backend.Engine/PluginRuntimeOrchestrator.cs) · [Result worker](../../Backend.Engine/PluginResultFinalizationWorker.cs) · [Code view](engine-execution.md)

## Progress Broadcaster

```mermaid
flowchart TB
  cosmos[("Cosmos DB · Step feed and realtime projections")]
  subgraph bc["Progress Broadcaster · .NET Functions app"]
    feed["Cosmos trigger or hosted change-feed processor"]
    batch["ProcessProgressBroadcaster<br/>Groups changed Steps by Process and node"]
    projection["ProcessProjectionService<br/>Builds batches from query projections"]
    gateway["SignalRProcessGateway<br/>Caches subscription snapshot and posts batches"]
    feed -->|Passes changed Steps| batch
    batch -->|Requests batch for subscribed groups| projection
    batch -->|Looks up groups and sends batch| gateway
  end
  cosmos -->|Delivers changed Steps| feed
  projection -->|Reads projections and repairs missing rows| cosmos
  gateway -->|Uses internal HTTP endpoints| hub["Backend.SignalR · ASP.NET Core"]
```

The SQL-backed Blueprint lookup resolves definition IDs for Blueprint subscribers. The broadcaster can skip work when nobody subscribes. SignalR delivers summary events to Blueprint groups and node events to Process groups. [Batch builder](../../Backend.Functions.ProgressBroadcaster/Services/ProcessProgressBroadcaster.cs) · [Internal gateway](../../Backend.Functions.ProgressBroadcaster/Services/SignalRProcessGateway.cs) · [SignalR group delivery](../../Backend.SignalR/Program.cs)

[Runtime interactions](interactions.md) · [Container views](containers.md) · [Architecture index](README.md)
