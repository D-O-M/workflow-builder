# WorkflowBuilder Agent Guide

WorkflowBuilder is a blueprint-driven orchestration app. Users design versioned blueprint graphs, compose phase series from those blueprints, start process instances, and inspect process progress and logs in near real time.

## Canonical Terms

- `Blueprint`: design-time graph definition
- `Process`: runtime instance of a blueprint
- `Step`: executed blueprint node in a process
- `TaskDefinition`: reusable task capability in the catalog
- `Plugin`: packaged implementation used to execute a task version
- `Plugin Capability`: engine prerequisite declared by a plugin, such as software, network, or configuration
- `Domain Plugin Configuration`: typed shared plugin config contracts under `Backend.Plugins.Configuration`
- `Phase Series`: ordered set of blueprint versions connected by Data Bridge mappings
- `Data Bridge`: phase handoff mapping between source outputs and destination inputs
- `Collection Iterator`: native engine operator for dynamic collection fan-out

`WorkflowBuilder` remains the product and repo token. New app-domain work should use `Blueprint / Process / Step` language.

## Main UI Surfaces

- `Dashboard`
- `Blueprint Builder`
- `Phases`
- `Start Process`
- `Process Explorer`

## Read Next

- [Overview](Docs/agent-context/overview.md)
- [Topology and Runtime](Docs/agent-context/topology-and-runtime.md)
- [Frontend Map](Docs/agent-context/frontend-map.md)
- [Plugin Development](Docs/agent-context/plugin-development.md)
- [Realtime and Observability](Docs/agent-context/realtime-and-observability.md)
- [Developer Workflows](Docs/agent-context/developer-workflows.md)

## Routing Hints

- Page, template, routing, Angular state, canvas, or Phases work: read `frontend-map.md`
- AppHost, Functions, queues, Cosmos, SignalR, or distributed runtime work: read `topology-and-runtime.md`
- Plugin bundle, harness, manifest, capability, configuration, or seeding work: read `plugin-development.md`
- Live updates, broadcaster issues, or runtime log flow: read `realtime-and-observability.md`
- Local commands, unit tests, Playwright, builds, and bundle rebuilds: read `developer-workflows.md`
