# WorkflowBuilder Overview

## Purpose

WorkflowBuilder lets users:

1. author and version blueprint graphs
2. start process instances from those blueprints
3. watch process progress, step state, and logs in near real time

At a high level, the product is a visual orchestration system backed by a distributed runtime.

## Core Mental Model

- `Blueprint Builder` is the design-time surface
- `Start Process` launches a runtime instance from a selected blueprint
- `Process Explorer` shows a readonly graph plus step details and logs
- `Dashboard` summarizes process outcomes and recent history

## Canonical Glossary

- `Blueprint`: the authored graph definition stored in SQL
- `BlueprintVersion`: a versioned snapshot of a blueprint
- `BlueprintNode`: a placed task inside a blueprint graph
- `TaskDefinition`: a reusable capability definition discovered from plugin bundles
- `TaskVersion`: a concrete executable version of a task definition
- `Process`: a runtime instance of a blueprint version
- `ProcessStep`: the runtime record for one executed blueprint node
- `Plugin`: the packaged executable and metadata that backs a task version

Avoid reintroducing old `workflow`, `execution`, or `run` wording for new domain-facing work.

## Repo Map

- `Backend.AppHost`: Aspire source of truth for topology and local orchestration
- `Backend.API`: main HTTP API for blueprints, processes, and client config
- `Backend.Engine`: process-step queue consumer and plugin runtime execution
- `Backend.Functions.PluginDetector`: bundle ingestion and task-definition registration
- `Backend.Functions.ProgressBroadcaster`: Cosmos-triggered realtime publisher
- `Backend.SignalR`: SignalR hub for live process subscriptions
- `Backend.Database.Relational`: SQL entities and migrations for blueprints and task metadata
- `Backend.State.DAL`: Cosmos-backed process and process-step state models
- `Backend.Plugins`: real plugin projects plus canonical `_bundles` output
- `UI`: Angular frontend
- `IntegrationTests`: distributed integration harness and smoke scripts

## Ownership Boundaries

- SQL owns design-time and catalog metadata:
  - blueprint definitions and versions
  - blueprint nodes and connections
  - task definitions and task versions
- Cosmos owns live runtime state:
  - processes
  - process steps
- Blob Storage owns uploaded and seeded plugin bundles
- Service Bus moves process-step work and plugin-reload messages
- SignalR owns live subscriptions and pushes

## Start Here by Task Type

- architecture, topology, services, queues, or data ownership:
  - read [topology-and-runtime.md](topology-and-runtime.md)
- Angular pages, routes, or client services:
  - read [frontend-map.md](frontend-map.md)
- plugin packaging, bundle rebuilds, harness, or seeding:
  - read [plugin-development.md](plugin-development.md)
- live updates, broadcaster, SignalR, or runtime logs:
  - read [realtime-and-observability.md](realtime-and-observability.md)
- local commands, builds, tests, and smoke runs:
  - read [developer-workflows.md](developer-workflows.md)
