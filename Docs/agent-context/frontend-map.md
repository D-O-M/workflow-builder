# Frontend Map

## Routes

`UI/src/app/app.routes.ts` defines the main route surface:

- `/dashboard` → `DashboardPageComponent`
- `/blueprints` → `BlueprintEditorPageComponent`
- `/processes/new` → `StartProcessPageComponent`
- `/processes` → `ProcessExplorerPageComponent`

## Page Responsibilities

### Dashboard

- shows process outcome summaries
- shows recent processes and node outcome summaries
- consumes observability endpoints through `observability.service.ts`

### Blueprint Builder

- authors and versions blueprint graphs
- uses the shared `BlueprintCanvasComponent`
- relies on:
  - `blueprint-editor.store.ts`
  - `builder-input.service.ts`
  - `canvas-wrapper.ts`
  - `blueprint-editor.service.ts`

### Start Process

- selects a blueprint and initial input
- starts a process by calling `POST /api/blueprints/{id}/processes`
- uses `blueprint-editor.service.ts`

### Process Explorer

- lists blueprints/processes and process history
- loads readonly process graphs and step details
- hosts the right-side step drawer
- relies on:
  - `blueprint-editor.service.ts` for process and step detail APIs
  - `process-realtime.service.ts` for live SignalR updates
  - `observability.service.ts` for step and process log access
  - `BlueprintCanvasComponent` in readonly mode

## Key Client Services

### `blueprint-editor.service.ts`

Canonical frontend API client for:

- blueprint CRUD/load
- start process
- process history
- process detail
- process step detail

### `observability.service.ts`

Owns observability-facing API access for:

- dashboard summaries
- recent processes
- process logs
- process-step logs
- step log SSE streams

### `process-realtime.service.ts`

Owns SignalR connection lifecycle for `/hubs/processes`:

- connects using runtime client-config
- subscribes by blueprint or process
- listens for:
  - `processSummaryUpdated`
  - `processStepsUpdated`
- batches and deduplicates updates before exposing them to pages

### `blueprint-editor.store.ts`

Owns interactive builder state:

- selection
- clipboard operations
- undo history
- hit-testing and canvas interactions

## Shared Canvas

`BlueprintCanvasComponent` is shared between:

- editable builder mode
- readonly process-explorer mode

Readonly process inspection should preserve the non-editable interaction boundary and add behavior through explicit readonly inputs/outputs rather than by leaking builder-edit semantics into the explorer.

## Frontend Change Routing

- route or page layout work:
  - start in `app.routes.ts` and the relevant `pages/*`
- API shape consumption:
  - start in `blueprint-editor.service.ts` or `observability.service.ts`
- live update behavior:
  - start in `process-realtime.service.ts`
- graph rendering or selection behavior:
  - start in `components/blueprint-canvas.component.*`
