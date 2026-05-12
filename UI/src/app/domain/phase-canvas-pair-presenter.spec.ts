import { describe, expect, it, vi } from 'vitest';
import { BlueprintCanvasEvent, CanvasOutputPropertyDragEvent } from '../components/blueprint-canvas-contract';
import { PhaseSeriesPhaseCanvas } from './phase-series-canvas';
import { PhaseCanvasPairPresenter } from './phase-canvas-pair-presenter';
import { PhaseCanvasPort } from './phase-workbench-contract';

function phase(overrides: Partial<PhaseSeriesPhaseCanvas> = {}): PhaseSeriesPhaseCanvas {
  return {
    phaseId: 'phase-1',
    name: 'Phase 1',
    blueprintName: 'Intake',
    blueprintDefinitionId: 1,
    blueprintVersionId: 101,
    blueprintVersionNumber: 1,
    nodes: [],
    connections: [],
    ...overrides,
  };
}

function sourceDragEvent(overrides: Partial<CanvasOutputPropertyDragEvent> = {}): CanvasOutputPropertyDragEvent {
  return {
    clientX: 100,
    clientY: 120,
    cancelled: false,
    nodeId: 7,
    nodeInstanceId: 'source-node',
    nodeName: 'Source',
    channelName: 'Output',
    propertyName: 'Result',
    propertyKind: 'String',
    ...overrides,
  };
}

function canvasPort(): PhaseCanvasPort {
  return {
    bridge: {
      outputPropertyClientPoint: vi.fn(() => null),
      inputPropertyClientPoint: vi.fn(() => null),
      hitTestInputPropertyAtClientPoint: vi.fn(() => null),
    },
    viewport: {
      reset: vi.fn(),
      zoomIn: vi.fn(),
      zoomOut: vi.fn(),
      focus: vi.fn(),
      centerOnNode: vi.fn(),
      centerOnBounds: vi.fn(),
      centerWorld: vi.fn(() => null),
      containsClientPoint: vi.fn(() => false),
    },
  };
}

describe('PhaseCanvasPairPresenter', () => {
  it('builds canvas graph and ambient bridge runtimes for both canvases', () => {
    const presenter = new PhaseCanvasPairPresenter();
    const viewportInsets = { top: 1, right: 2, bottom: 3, left: 4 };

    expect(presenter.graph(phase(), 12)).toEqual({
      nodes: [],
      connections: [],
      revision: 12,
    });
    expect(presenter.sourceRuntime(viewportInsets)).toEqual({
      mode: 'bridge-source',
      backgroundEffect: 'stitch',
      connectionPresentation: 'ambient',
      viewportInsets,
    });
    expect(presenter.destinationRuntime(viewportInsets)).toEqual({
      mode: 'bridge-target',
      backgroundEffect: 'stitch',
      connectionPresentation: 'ambient',
      viewportInsets,
    });
    expect(presenter.selection()).toEqual({
      selectedNodeIds: [],
      selectedConnectionIds: [],
      readonlySelectedNodeId: null,
      canvasFocused: false,
      modifiers: { shift: false, ctrlOrMeta: false, space: false },
    });
  });

  it('routes source canvas events to viewport and bridge handlers', () => {
    const presenter = new PhaseCanvasPairPresenter();
    const viewportChanged = vi.fn();
    const sourceDragStarted = vi.fn();
    const sourceDragMoved = vi.fn();
    const sourceDragEnded = vi.fn();
    const startedEvent: BlueprintCanvasEvent = {
      kind: 'bridgeOutputPropertyDrag',
      phase: 'started',
      drag: sourceDragEvent(),
    };

    presenter.handleSourceEvent({ kind: 'viewportChanged' }, {
      viewportChanged,
      sourceDragStarted,
      sourceDragMoved,
      sourceDragEnded,
    });
    presenter.handleSourceEvent(startedEvent, {
      viewportChanged,
      sourceDragStarted,
      sourceDragMoved,
      sourceDragEnded,
    });
    presenter.handleSourceEvent({
      ...startedEvent,
      phase: 'moved',
    }, {
      viewportChanged,
      sourceDragStarted,
      sourceDragMoved,
      sourceDragEnded,
    });
    presenter.handleSourceEvent({
      ...startedEvent,
      phase: 'ended',
    }, {
      viewportChanged,
      sourceDragStarted,
      sourceDragMoved,
      sourceDragEnded,
    });

    expect(viewportChanged).toHaveBeenCalledTimes(1);
    expect(sourceDragStarted).toHaveBeenCalledWith(startedEvent.drag);
    expect(sourceDragMoved).toHaveBeenCalledWith(startedEvent.drag);
    expect(sourceDragEnded).toHaveBeenCalledWith(startedEvent.drag);
  });

  it('routes destination canvas viewport changes and viewport commands', () => {
    const presenter = new PhaseCanvasPairPresenter();
    const viewportChanged = vi.fn();
    const canvas = canvasPort();

    presenter.handleDestinationEvent({ kind: 'viewportChanged' }, { viewportChanged });
    presenter.reset(canvas);
    presenter.zoomIn(canvas);
    presenter.zoomOut(canvas);

    expect(viewportChanged).toHaveBeenCalledOnce();
    expect(canvas.viewport.reset).toHaveBeenCalledOnce();
    expect(canvas.viewport.zoomIn).toHaveBeenCalledOnce();
    expect(canvas.viewport.zoomOut).toHaveBeenCalledOnce();
  });
});
