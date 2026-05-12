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
  graph(phase: PhaseSeriesPhaseCanvas, revision: number): BlueprintCanvasGraph {
    return {
      nodes: phase.nodes,
      connections: phase.connections,
      revision,
    };
  }

  sourceRuntime(viewportInsets: CanvasViewportInsets): BlueprintCanvasRuntime {
    return this.runtime('bridge-source', viewportInsets);
  }

  destinationRuntime(viewportInsets: CanvasViewportInsets): BlueprintCanvasRuntime {
    return this.runtime('bridge-target', viewportInsets);
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

  private runtime(mode: BlueprintCanvasRuntime['mode'], viewportInsets: CanvasViewportInsets): BlueprintCanvasRuntime {
    return {
      mode,
      backgroundEffect: 'stitch',
      connectionPresentation: 'ambient',
      viewportInsets,
    };
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
