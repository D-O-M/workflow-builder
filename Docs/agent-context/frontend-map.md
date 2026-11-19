# Frontend Map

## Stack and Routing

The UI is an Angular 21 app under `UI/`.

`UI/src/app/app.routes.ts` defines the main route surface:

- `/dashboard` -> `DashboardPageComponent`
- `/blueprints` -> `BlueprintEditorPageComponent`
- `/phases` -> `PhasesPageComponent`
- `/processes/new` -> `StartProcessPageComponent`
- `/processes` -> `ProcessExplorerPageComponent`
- `/processes/phase-series` and `/processes/phase-series/:definitionId/:parentId` -> Phase Series search and parent overview

The product routes use `authGuard`. The app also handles the OIDC callbacks at `/auth/callback` and `/auth/logout-callback`. The shell gets its primary navigation labels from `UI/src/app/domain/app-navigation.ts`. [Sign-in and runtime client config](../authentication-keycloak.md)

## Page Responsibilities

### Dashboard

- shows process outcome summaries
- shows recent processes and node outcome summaries
- consumes observability endpoints through `observability.service.ts`

### Blueprint Builder

- authors and versions blueprint graphs
- owns builder-specific panels, task library, validation flow, and save/version actions
- uses `BlueprintCanvasComponent` through the grouped canvas public contract
- maps canvas events to store mutations through `blueprint-canvas-store-action-utility.ts`
- relies on:
  - `blueprint-editor.store.ts`
  - `blueprint-editor-store-contract.ts`
  - `blueprint-editor-node-factory.ts`
  - `blueprint-editor-connection-policy.ts`
  - `blueprint-editor-graph-snapshot-utility.ts`
  - `blueprint-editor-selection-utility.ts`
  - `blueprint-editor.service.ts`

### Phases

- authors phase series made from 2 to 4 blueprint versions
- shows paired source/destination readonly phase canvases around a Data Bridge divider
- supports horizontal and vertical split modes
- stages source output properties on the bridge, then maps them to compatible destination input properties
- keeps ordinary internal blueprint connections visible as ambient context while Data Bridge curves remain visually dominant
- relies on:
  - `phases-page.component.*`
  - `phase-series-canvas.ts`
  - `phase-workbench-contract.ts`
  - `phase-bridge-controller.ts`
  - `phase-bridge-curve-factory.ts`
  - `phase-canvas-pair-presenter.ts`
  - `phase-bridge-geometry.ts`

The Phases page is intentionally still the service/loading/saving shell. Data Bridge drag state, curve construction, and canvas-pair contract assembly have been moved into domain controllers/factories/presenters.

### Start Process

- accepts a Blueprint or Phase Series selection, the first task's schema-backed input and optional node overrides
- starts a Blueprint through `POST /api/blueprints/{id}/processes`, or a Phase Series through `POST /api/phased-blueprints/{id}/processes`
- opens the accepted Process for a Blueprint, or the parent overview for a Phase Series

[First Process tutorial](../tutorials/first-process.md) · [Phase Series handoff and trigger](../how-to/phase-series.md)

### Process Explorer

- lists blueprints/processes and process history
- finds started Phase Series by name, parent ID, and status, and links their ordered phases to child Processes
- uses `phase-series-explorer.service.ts` for paginated parent search, overview refresh, and on-demand Data Bridge values
- loads readonly process graphs and step details
- hosts the right-side step drawer, tabs, logs, and advanced search
- relies on:
  - `blueprint-editor.service.ts` for process and step detail APIs
  - `process-realtime.service.ts` for live SignalR updates
  - `observability.service.ts` for step and process log access
  - `BlueprintCanvasComponent` in readonly mode

## Key Client Services

### `blueprint-editor.service.ts`

Canonical frontend API client for:

- blueprint CRUD/load
- phased blueprint list/load/create/version
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

- graph mutation sequencing and revision bumps
- selection, clipboard, undo/redo history, and modifiers
- node creation/version/override updates through domain request models
- connection creation, Data Bridge mappings, and node version reconciliation

The store public API is request-object based. Prefer the domain request models in `blueprint-editor-store-contract.ts` over new long parameter lists.

## Shared Canvas Contract

`BlueprintCanvasComponent` is shared between:

- editable builder mode
- readonly process-explorer mode
- phase source and phase target bridge modes

The public canvas API is grouped in `UI/src/app/components/blueprint-canvas-contract.ts`:

- `[graph]`: nodes, connections, revision
- `[runtime]`: mode, background effect, connection presentation, viewport insets
- `[selection]`: selected nodes/connections, readonly selected node, focus, modifiers
- `[validation]`: validation focus and issue markers
- `(canvasEvent)`: a single typed event stream for selection, node settings, readonly activation, graph edits, bridge drag phases, focus/history, and viewport changes

The public imperative ports are:

- `canvas.viewport.*`
- `canvas.validation.*`
- `canvas.bridge.*`

Do not reintroduce prop-by-prop canvas inputs or output-per-action APIs.

## Canvas Internals

`BlueprintCanvasComponent` should stay a thin Angular shell for lifecycle, DOM events, output emission, and draw scheduling.

Key internal layers:

- `canvas-renderer-host.ts` and `canvas-offscreen-render.worker.ts`: choose the main-thread renderer or hand a supported canvas to a worker when offscreen rendering is enabled
- `canvas-mode-policy.ts`: mode capabilities for `edit`, `readonly`, `bridge-map`, `bridge-source`, and `bridge-target`
- `canvas-viewport-controller.ts`: zoom, pan, backing-size sync, insets, and client/world math
- `canvas-interaction-controller.ts`: pointer lifecycle state and explicit interaction effects
- `canvas-hit-tester.ts`: node/connection/port hit testing
- `canvas-validation-focus-resolver.ts`: validation focus resolution and issue summaries
- `canvas-render-frame-factory.ts`: draw-plan assembly from snapshot and interaction state
- `canvas-bridge-adapter.ts`: phase bridge property hit testing and drag payloads

Renderer layers:

- `canvas-wrapper.ts`: compatibility facade and draw coordinator
- `canvas-theme.ts`
- `canvas-draw-surface.ts`
- `canvas-backdrop-renderer.ts`
- `canvas-connection-renderer.ts`
- `canvas-connection-preview-renderer.ts`
- `canvas-node-renderer.ts`
- `canvas-overlay-renderer.ts`
- `canvas-connection-drawing-utility.ts`

Render-model layers are split behind the `canvas-render-model.ts` barrel:

- `canvas-render-node-model.ts`
- `canvas-render-connection-model.ts`
- `canvas-render-snapshot.ts`
- `canvas-render-connection-routing.ts`
- `canvas-render-lane-shade.ts`
- `canvas-render-spatial-index.ts`
- `canvas-render-geometry-utilities.ts`
- `canvas-render-types.ts`

Node layout is split behind `node-model.ts`:

- `canvas-node-layout-factory.ts`
- `canvas-node-layout-cache.ts`
- `canvas-node-geometry-utility.ts`

Use `utility`, `renderer`, `surface`, `theme`, `policy`, `controller`, `factory`, `resolver`, `presenter`, or `contract` terminology. Avoid introducing new `helper` names.

## Phase Canvas Performance Notes

The Phases route is the first canvas performance-sensitive surface.

Current optimizations:

- `PhaseCanvasPairPresenter` caches canvas graph/runtime contracts so pan/zoom and bridge drag do not churn input identities.
- `BlueprintCanvasComponent` emits `viewportChanged` at most once per animation frame, leading-edge, so bridge curves stay live without event spam.
- `bridge-source` and `bridge-target` modes enable viewport culling for drawing only; full render snapshots remain available for hit testing, bridge ports, validation, and route errors.
- `PhaseBridgeCurveFactory` and `PhasesPageComponent` keep bridge curves rAF-scheduled and avoid setting curve signals when the curve plan has not changed.

When changing phase pan/zoom, verify bridge curves remain live in horizontal and vertical split modes.

## Frontend Test Surface

Stable `data-testid` attributes are part of the maintained frontend test contract.

The passive `?e2e=1` canvas inspection seam exposes canvas snapshots for Playwright pointer interactions:

- node bounds
- port points
- section state
- zoom and viewport state

The seam must remain readonly and must not mutate application state or alter production behavior.

## Frontend Change Routing

- route or page layout work:
  - start in `app.routes.ts`, `app-navigation.ts`, and the relevant `pages/*`
- API shape consumption:
  - start in `blueprint-editor.service.ts` or `observability.service.ts`
- live update behavior:
  - start in `process-realtime.service.ts`
- graph rendering, canvas performance, or selection behavior:
  - start in `components/blueprint-canvas.component.*` and the canvas domain layers
- Phases or Data Bridge behavior:
  - start in `pages/phases-page.component.*` and `domain/phase-*`
