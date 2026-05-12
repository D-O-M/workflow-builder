---
name: workflow-builder
description: >
  Repo-local orchestrator skill for the WorkflowBuilder application. Use when working in this
  repository or when the task mentions blueprints, processes, process explorer, AppHost,
  phases, Data Bridge, canvas behavior, Playwright, plugin bundles, plugin seeding, SignalR live
  updates, progress broadcasting, or process-step queue behavior. This skill teaches the app's
  purpose, routes to the right repo docs, and conditionally loads the best companion skills for
  Aspire, Angular, frontend testing, and realtime work.
---

# WorkflowBuilder

Start every task here:

Locate the WorkflowBuilder checkout root with `git rev-parse --show-toplevel`. Resolve repository paths from that root and companion skill names through the active session's skill catalog.

1. Read `AGENTS.md`.
2. Read only the relevant docs under `Docs/agent-context/`.
3. Load companion skills only for the task areas that need them.

Do not load every companion skill by default.

## Repo Purpose

WorkflowBuilder is a blueprint-driven orchestration application:

- users author versioned blueprint graphs
- users compose phased blueprint series with Data Bridge mappings
- users start runtime process instances from blueprints or phased blueprint series
- users inspect live process progress, step details, and logs in the UI

The app depends on a working distributed runtime and a working realtime path.

## Canonical Terminology

- `Blueprint` = design-time graph definition
- `Process` = runtime instance of a blueprint
- `Step` = executed blueprint node in a process
- `TaskDefinition` = reusable capability in the catalog
- `Plugin` = packaged executable implementation for a task version
- `Phase Series` = ordered set of blueprint versions connected by Data Bridge mappings
- `Data Bridge` = phase handoff mapping between source output properties and destination inputs
- `Collection Iterator` = native engine operator for collection fan-out, not a plugin

Do not reintroduce old `workflow`, `execution`, or `run` terminology in new user-facing or domain-facing changes.

## Repo Docs to Read

- `Docs/agent-context/overview.md`
- `Docs/agent-context/topology-and-runtime.md`
- `Docs/agent-context/frontend-map.md`
- `Docs/agent-context/plugin-development.md`
- `Docs/agent-context/realtime-and-observability.md`
- `Docs/agent-context/developer-workflows.md`

## Task Routing

### AppHost, Functions, topology, resources, queues, service wiring, distributed integration

Read:

- `Docs/agent-context/topology-and-runtime.md`
- `Docs/agent-context/developer-workflows.md`

Load:

- `$dotnet-aspire`

Also load:

- `$aspire`

only when the task involves starting, stopping, describing, logging, or debugging the distributed app runtime.

### Angular pages, components, routing, state, styling, and templates

Read:

- `Docs/agent-context/frontend-map.md`

Load:

- `$angular-developer`

### Phases, Data Bridge, shared canvas, canvas performance, or Playwright E2E work

Read:

- `Docs/agent-context/frontend-map.md`
- `Docs/agent-context/developer-workflows.md`

Load:

- `$angular-developer`

Also load:

- `$frontend-design`

when the work changes UX, layout, visual hierarchy, interaction affordances, or responsive behavior.

### Realtime updates, progress broadcaster, SignalR, OTEL, observability, live logs

Read:

- `Docs/agent-context/realtime-and-observability.md`
- `Docs/agent-context/topology-and-runtime.md`

Load:

- `$configuring-opentelemetry-dotnet`

Also load:

- `$dotnet-aspire`

when the issue includes AppHost or Functions wiring.

### Plugin bundles, harness, manifests, seeding, task-version registration

Read:

- `Docs/agent-context/plugin-development.md`
- `Docs/agent-context/developer-workflows.md`
- `Docs/agent-context/topology-and-runtime.md`

Load:

- `$dotnet-aspire`

when the task touches AppHost seeding, blob wiring, detector registration, or engine/plugin runtime behavior.

### General backend feature work

Read:

- `Docs/agent-context/overview.md`
- `Docs/agent-context/topology-and-runtime.md`

Then use normal engineering reasoning. Do not force an unrelated generic skill.

## Repo-Specific Invariants

- AppHost is the source of truth for distributed wiring.
- Realtime is a first-class product requirement.
- Plugin bundles are built through `BuildScripts/bundle-plugins.ps1`, not by ad hoc zipping.
- `Backend.Plugins/_bundles` is ignored by git and is meant to be seeded on wiped startup.
- If harness or communication contracts change, rebuild canonical plugin bundles before trusting seeded or uploaded plugins.
- `Collection Iterator` is engine-native; do not model it as a plugin bundle.
- The shared canvas public contract is grouped in `blueprint-canvas-contract.ts`; do not reintroduce prop-by-prop canvas APIs.
- Canvas internals use `utility`, `renderer`, `surface`, `theme`, `policy`, `controller`, `factory`, `resolver`, `presenter`, or `contract` terminology. Avoid new `helper` names.
- Stable `data-testid` attributes and the readonly `?e2e=1` canvas inspection seam are maintained frontend test contracts.
- The required live-update event names are:
  - `processSummaryUpdated`
  - `processStepsUpdated`

## Plugin Build Guidance

Canonical build script:

- `BuildScripts/bundle-plugins.ps1`

Integration helper:

- `IntegrationTests/scripts/build-integration-plugin-bundles.ps1`

Canonical output path:

- `Backend.Plugins/_bundles`

Do not hand-edit bundle ZIP structure or use manual version bumps as a substitute for the canonical bundle script.

## UI Validation Guidance

Frontend confidence lane:

- `Set-Location UI`
- `npm run test:unit`
- `npm run build`
- `npm run test:e2e`

Runtime smoke lane, only when AppHost is already running:

- `Set-Location UI`
- `npm run test:e2e:runtime`

The Angular build currently passes with an initial bundle budget warning; new build errors are blockers.

## Realtime Diagnostic Frame

When a process-progress issue appears, classify it as:

- execution only
- broadcaster only
- SignalR or client-config only
- end-to-end realtime path breakage

Check the runtime path in this order:

1. process state is advancing
2. broadcaster is running and bound
3. SignalR subscriptions are healthy
4. UI listeners and client-config are pointed at the correct hub
