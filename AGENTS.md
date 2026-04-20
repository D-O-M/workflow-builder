# WorkflowBuilder Agent Guide

WorkflowBuilder is a blueprint-driven orchestration app. Users design versioned blueprint graphs, start process instances from those blueprints, and inspect process progress and logs in near real time.

## Canonical Terms

- `Blueprint`: design-time graph definition
- `Process`: runtime instance of a blueprint
- `Step`: executed blueprint node in a process
- `TaskDefinition`: reusable capability in the catalog
- `Plugin`: packaged implementation used to execute a task version

`WorkflowBuilder` remains the product and repo token. New app-domain work should use `Blueprint / Process / Step` language.

## Main UI Surfaces

- `Dashboard`
- `Blueprint Builder`
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

- Page, template, routing, or Angular state work: read `frontend-map.md`
- AppHost, Functions, queues, Cosmos, SignalR, or distributed runtime work: read `topology-and-runtime.md`
- Plugin bundle, harness, manifest, or seeding work: read `plugin-development.md`
- Live updates, broadcaster issues, or runtime log flow: read `realtime-and-observability.md`
- Local commands, builds, tests, and bundle rebuilds: read `developer-workflows.md`
