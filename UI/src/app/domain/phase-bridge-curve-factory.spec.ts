import { describe, expect, it, vi } from 'vitest';
import { DataBridgeMappingDraft, PhaseSeriesTransitionCanvas } from './phase-series-canvas';
import { PhaseBridgeCurveFactory } from './phase-bridge-curve-factory';
import { PhaseCanvasPort, PhaseWorkbenchDomRect } from './phase-workbench-contract';

const dividerRect: PhaseWorkbenchDomRect = {
  left: 100,
  right: 124,
  top: 50,
  bottom: 150,
  width: 24,
  height: 100,
};

function mapping(overrides: Partial<DataBridgeMappingDraft> = {}): DataBridgeMappingDraft {
  return {
    id: 'mapping-1',
    sourceNodeId: 7,
    sourceBlueprintNodeId: 'source-node',
    sourceNodeName: 'Source',
    sourceChannelName: 'Output',
    sourceProperty: 'Result',
    propertyKind: 'String',
    bridgePosition: 0.5,
    targetNodeId: 9,
    targetBlueprintNodeId: 'target-node',
    targetNodeName: 'Target',
    targetProperty: 'Input',
    ...overrides,
  };
}

function transition(mappings: readonly DataBridgeMappingDraft[]): PhaseSeriesTransitionCanvas {
  return {
    transitionId: 'transition-1',
    sourcePhaseId: 'source-phase',
    destinationPhaseId: 'destination-phase',
    mappings: [...mappings],
  };
}

function canvasPort(points: {
  readonly output?: { clientX: number; clientY: number } | null;
  readonly input?: { clientX: number; clientY: number } | null;
  readonly contains?: boolean;
}): PhaseCanvasPort {
  return {
    bridge: {
      outputPropertyClientPoint: vi.fn(() => points.output ?? null),
      inputPropertyClientPoint: vi.fn(() => points.input ?? null),
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
      containsClientPoint: vi.fn(() => points.contains ?? true),
    },
  };
}

describe('PhaseBridgeCurveFactory', () => {
  it('maps client points onto horizontal and vertical bridge positions', () => {
    const factory = new PhaseBridgeCurveFactory();
    const horizontalRect = { left: 10, right: 110, top: 20, bottom: 40, width: 100, height: 20 };
    const verticalRect = { left: 20, right: 40, top: 10, bottom: 110, width: 20, height: 100 };

    expect(factory.bridgePositionFromClientPoint(60, 20, horizontalRect, 'horizontal')).toBe(0.5);
    expect(factory.bridgePositionFromClientPoint(60, 70, horizontalRect, 'horizontal')).toBeNull();
    expect(factory.bridgePositionFromClientPoint(20, 60, verticalRect, 'vertical')).toBe(0.5);
    expect(factory.bridgePositionFromClientPoint(80, 60, verticalRect, 'vertical')).toBeNull();
  });

  it('creates source and destination bridge curves with issue state', () => {
    const factory = new PhaseBridgeCurveFactory();
    const plan = factory.createCurvePlan({
      workbenchRect: { left: 0, top: 0 },
      dividerRect,
      splitMode: 'horizontal',
      transition: transition([mapping()]),
      drag: null,
      sourceCanvas: canvasPort({ output: { clientX: 10, clientY: 20 } }),
      destinationCanvas: canvasPort({ input: { clientX: 220, clientY: 90 } }),
      mappingIssueMessage: () => 'Type mismatch.',
    });

    expect(plan.dragCurve).toBeNull();
    expect(plan.curves).toEqual([
      expect.objectContaining({
        id: 'mapping-1:input',
        mappingId: 'mapping-1',
        side: 'source',
        state: 'issue',
      }),
      expect.objectContaining({
        id: 'mapping-1:output',
        mappingId: 'mapping-1',
        side: 'destination',
        state: 'issue',
      }),
    ]);
    expect(plan.curves[0]?.path).toContain('C 44 20');
  });

  it('clips mapping curves when the node property is outside its canvas viewport', () => {
    const factory = new PhaseBridgeCurveFactory();
    const plan = factory.createCurvePlan({
      workbenchRect: { left: 0, top: 0 },
      dividerRect,
      splitMode: 'horizontal',
      transition: transition([
        mapping({
          targetNodeId: null,
          targetBlueprintNodeId: null,
          targetNodeName: null,
          targetProperty: null,
        }),
      ]),
      drag: null,
      sourceCanvas: canvasPort({
        output: { clientX: 10, clientY: 220 },
        contains: false,
      }),
      destinationCanvas: undefined,
      mappingIssueMessage: () => null,
    });

    expect(plan.curves).toEqual([]);
  });

  it('creates preview curves for source and bridge-output drags', () => {
    const factory = new PhaseBridgeCurveFactory();
    const sourcePreview = factory.createCurvePlan({
      workbenchRect: { left: 0, top: 0 },
      dividerRect,
      splitMode: 'vertical',
      transition: transition([mapping()]),
      drag: {
        kind: 'source-output',
        clientX: 150,
        clientY: 70,
        label: 'Source.Result',
        propertyKind: 'String',
        mappingId: null,
        bridgePosition: 0.2,
        sourceNodeId: 7,
        sourceChannelName: 'Output',
        sourceProperty: 'Result',
      },
      sourceCanvas: canvasPort({ output: { clientX: 10, clientY: 20 } }),
      destinationCanvas: undefined,
      mappingIssueMessage: () => null,
    });
    const outputPreview = factory.createCurvePlan({
      workbenchRect: { left: 0, top: 0 },
      dividerRect,
      splitMode: 'vertical',
      transition: transition([mapping()]),
      drag: {
        kind: 'bridge-output',
        clientX: 240,
        clientY: 120,
        label: 'Result',
        propertyKind: 'String',
        mappingId: 'mapping-1',
      },
      sourceCanvas: undefined,
      destinationCanvas: undefined,
      mappingIssueMessage: () => null,
    });

    expect(sourcePreview.dragCurve).toMatch(/^M 10 20 C 44 20/);
    expect(outputPreview.dragCurve).toMatch(/^M 124 100 C/);
  });
});
