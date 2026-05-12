import {
  BlueprintCanvasEvent,
  BlueprintCanvasGraph,
  BlueprintCanvasRuntime,
  BlueprintCanvasSelection,
  CanvasViewportInsets,
} from '../components/blueprint-canvas-contract';
import {
  PhaseCanvasEventHandlers,
  PhaseCanvasPort,
  PhaseCanvasPresenter,
} from './phase-workbench-contract';
import { PhaseSeriesPhaseCanvas } from './phase-series-canvas';

const EMPTY_PHASE_CANVAS_SELECTION: BlueprintCanvasSelection = {
  selectedNodeIds: [],
  selectedConnectionIds: [],
  readonlySelectedNodeId: null,
  canvasFocused: false,
  modifiers: { shift: false, ctrlOrMeta: false, space: false },
};

export class PhaseCanvasPairPresenter implements PhaseCanvasPresenter {
  private readonly graphCache = new WeakMap<PhaseSeriesPhaseCanvas, {
    readonly revision: number;
    readonly graph: BlueprintCanvasGraph;
  }>();
  private readonly sourceRuntimeCache = new WeakMap<CanvasViewportInsets, BlueprintCanvasRuntime>();
  private readonly destinationRuntimeCache = new WeakMap<CanvasViewportInsets, BlueprintCanvasRuntime>();

  graph(phase: PhaseSeriesPhaseCanvas, revision: number): BlueprintCanvasGraph {
    const cached = this.graphCache.get(phase);
    if (cached?.revision === revision) {
      return cached.graph;
    }

    const graph = {
      nodes: phase.nodes,
      connections: phase.connections,
      revision,
    };
    this.graphCache.set(phase, { revision, graph });
    return graph;
  }

  sourceRuntime(viewportInsets: CanvasViewportInsets): BlueprintCanvasRuntime {
    return this.cachedRuntime(this.sourceRuntimeCache, 'bridge-source', viewportInsets);
  }

  destinationRuntime(viewportInsets: CanvasViewportInsets): BlueprintCanvasRuntime {
    return this.cachedRuntime(this.destinationRuntimeCache, 'bridge-target', viewportInsets);
  }

  selection(): BlueprintCanvasSelection {
    return EMPTY_PHASE_CANVAS_SELECTION;
  }

  handleSourceEvent(event: BlueprintCanvasEvent, handlers: PhaseCanvasEventHandlers): void {
    this.handleSharedEvent(event, handlers);
    if (event.kind !== 'bridgeOutputPropertyDrag') {
      return;
    }

    switch (event.phase) {
      case 'started':
        handlers.sourceDragStarted?.(event.drag);
        break;
      case 'moved':
        handlers.sourceDragMoved?.(event.drag);
        break;
      case 'ended':
        handlers.sourceDragEnded?.(event.drag);
        break;
    }
  }

  handleDestinationEvent(
    event: BlueprintCanvasEvent,
    handlers: Pick<PhaseCanvasEventHandlers, 'viewportChanged'>,
  ): void {
    this.handleSharedEvent(event, handlers);
  }

  reset(canvas: PhaseCanvasPort | undefined): void {
    canvas?.viewport.reset();
  }

  zoomIn(canvas: PhaseCanvasPort | undefined): void {
    canvas?.viewport.zoomIn();
  }

  zoomOut(canvas: PhaseCanvasPort | undefined): void {
    canvas?.viewport.zoomOut();
  }

  private cachedRuntime(
    cache: WeakMap<CanvasViewportInsets, BlueprintCanvasRuntime>,
    mode: BlueprintCanvasRuntime['mode'],
    viewportInsets: CanvasViewportInsets,
  ): BlueprintCanvasRuntime {
    const cached = cache.get(viewportInsets);
    if (cached) {
      return cached;
    }

    const runtime: BlueprintCanvasRuntime = {
      mode,
      backgroundEffect: 'stitch',
      connectionPresentation: 'ambient',
      viewportInsets,
    };
    cache.set(viewportInsets, runtime);
    return runtime;
  }

  private handleSharedEvent(
    event: BlueprintCanvasEvent,
    handlers: Pick<PhaseCanvasEventHandlers, 'viewportChanged'>,
  ): void {
    if (event.kind === 'viewportChanged') {
      handlers.viewportChanged();
    }
  }
}
