# WorkflowBuilder Agent Guide

WorkflowBuilder is a blueprint-driven orchestration app. Users design versioned blueprint graphs, compose phase series from those blueprints, start process instances, and inspect process progress and logs in near real time.

## Canonical Terms

- `Blueprint`: design-time graph definition
- `Process`: runtime instance of a blueprint
- `Step`: executed blueprint node in a process
- `TaskDefinition`: reusable capability in the catalog
- `Plugin`: packaged implementation used to execute a task version
- `Phase Series`: ordered set of blueprint versions connected by Data Bridge mappings

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
- Plugin bundle, harness, manifest, or seeding work: read `plugin-development.md`
- Live updates, broadcaster issues, or runtime log flow: read `realtime-and-observability.md`
- Local commands, unit tests, Playwright, builds, and bundle rebuilds: read `developer-workflows.md`
