# Containers · WorkflowBuilder

**C4 Level 2.** Each diagram shows a slice of the same system. Repeated names denote the same application or store. The diagrams group the product by the work it does. Environment-specific placement and optional services are in the [deployment views](deployment.md).

## Starting and executing a Process

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
  web["Angular UI · browser"] -->|Starts and queries Processes over HTTP| api["API · ASP.NET Core"]
  api -->|Loads versioned graphs| sql[("SQL Server · design and catalog")]
  api -->|Creates Process, first Step and dispatch intent| cosmos[("Cosmos DB · runtime records")]
  api -->|Attempts immediate send| bus[["Service Bus · processsteps"]]
  dispatch["Dispatcher · .NET worker"] -->|Claims due dispatch intents| cosmos
  dispatch -->|Sends scheduled Steps| bus
  bus -->|Delivers Step messages| engine["Engine · .NET service"]
  engine -->|Loads the executable graph| sql
  engine -->|Claims and finalizes Steps| cosmos
  engine -->|Sends jobs over stdin and reads stdout| plugin["Plugin executable · child process"]
  plugin -->|Reads input channels| cosmos
```

An external API client can start the same Process. Engine can also send newly scheduled Steps immediately. A durable Cosmos intent lets Dispatcher retry a send. Maintenance is a separate .NET worker which can repair an incomplete Process start or resume a Step finalization from its checkpoint. The Collection Iterator executes inside Engine. [Lifecycle and guarantees](../agent-context/topology-and-runtime.md) · [Engine components](components.md#engine)

## Observing a Process

```mermaid
flowchart TB
  cosmos[("Cosmos DB · Process and Step records")]
  proj["Projection Worker · .NET worker"] -->|Consumes change feeds and writes query projections| cosmos
  bc["Progress Broadcaster · .NET Functions app"] -->|Consumes Step changes and reads realtime projections| cosmos
  bc -->|Requests subscriptions and posts batches over HTTP| hub["SignalR · ASP.NET Core"]
  hub -->|Sends group events to subscribed browsers| ui["Angular UI · Process Explorer"]
  ui -->|Queries details and streams logs over HTTP| api["API · ASP.NET Core"]
  api -->|Reads Process state and query projections| cosmos
  api -->|Queries Process and Step logs| loki[("Loki · log store when enabled")]
```

Dashboard gets summaries over the observability HTTP API. SignalR delivers progress events to Process Explorer. Live Process and Step logs use server-sent events (SSE) from the API. When configured, the observability stack collects traces, logs and metrics and exposes them in Grafana. [Interactions](interactions.md) · [Observability endpoints](../../Backend.API/Controllers/ObservabilityController.cs)

## Registering and loading Plugins

```mermaid
flowchart TB
  seed["Plugin Seeder · .NET job"] -->|Uploads canonical bundles| blob[("Blob Storage · Plugin ZIPs")]
  blob -->|Supplies new or discovered bundles| detector["Plugin Detector · .NET Functions app"]
  detector -->|Registers TaskDefinitions and versions| sql[("SQL Server · task catalog")]
  detector -->|Records ingestion outcomes| cosmos[("Cosmos DB · operational status")]
  detector -->|Requests reload| ctl[["Service Bus · pluginreloadqueue"]]
  ctl -->|Delivers reload request| eng["Engine · .NET service"]
  eng -->|Downloads versioned bundles| blob
  eng -->|Reads registered task versions| sql
  eng -->|Publishes per-instance runtime availability| cosmos
```

The Seeder also calls the same bundle processor as Detector, so seeded bundles can register and request reload directly. Engine validates declared capabilities, starts the executable and registers its agent as runnable. A version can appear in the SQL catalog before it becomes runnable on a particular Engine. [Bundle registration](../../Backend.Services/PluginBundleProcessingService.cs) · [Agent startup](../../Backend.Engine/PluginRuntimeOrchestrator.cs)

| Other application or store | Responsibility |
| --- | --- |
| Migration Service · .NET job | Applies relational migrations and configured runtime backfills. |
| Blueprint Seeder · .NET job | Imports seed Blueprints through the API after its prerequisites are ready. |
| Blob Storage · artifacts | Stores artifact content separately from Plugin bundle ZIPs. |
| Keycloak · OIDC provider | Issues identity tokens when authentication is enabled. |

[Explore the components](components.md) · [Deployment](deployment.md) · [Architecture index](README.md)
