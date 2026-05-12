import { describe, expect, it } from 'vitest';
import { canvasModeCapabilities, canStartCanvasConnectionFrom, isPassiveCanvasSummaryPort } from './canvas-mode-policy';

function node(isDataBridge = false, dataBridgeKind: 'input' | 'output' | null = null) {
  return {
    base: {
      isDataBridge,
      dataBridgeKind,
    },
  };
}

function port(kind: string, anchorIndex: number | null = 0) {
  return {
    kind,
    anchorIndex,
  };
}

describe('canvasModeCapabilities', () => {
  it('keeps edit mode graph-first and connection-selectable', () => {
    expect(canvasModeCapabilities('edit')).toEqual({
      readOnly: false,
      graphEditable: true,
      connectionMapping: true,
      passiveSummaryPorts: false,
      dragConnectionPreview: true,
      selectableConnections: true,
    });
  });

  it('makes bridge source mode property-only with passive summary anchors', () => {
    expect(canvasModeCapabilities('bridge-source')).toMatchObject({
      readOnly: true,
      graphEditable: false,
      connectionMapping: true,
      passiveSummaryPorts: true,
      dragConnectionPreview: false,
    });
    expect(isPassiveCanvasSummaryPort('bridge-source', { kind: 'output-summary' })).toBe(true);
    expect(isPassiveCanvasSummaryPort('bridge-source', { kind: 'output-property' })).toBe(false);
  });
});

describe('canStartCanvasConnectionFrom', () => {
  it('allows only non-bridge output properties in bridge-source mode', () => {
    expect(canStartCanvasConnectionFrom('bridge-source', node(), port('output-property'))).toBe(true);
    expect(canStartCanvasConnectionFrom('bridge-source', node(), port('output-summary'))).toBe(false);
    expect(canStartCanvasConnectionFrom('bridge-source', node(true, 'input'), port('output-property'))).toBe(false);
  });

  it('keeps bridge-map starts property-only and scoped to bridge input nodes', () => {
    expect(canStartCanvasConnectionFrom('bridge-map', node(), port('output-property'))).toBe(true);
    expect(canStartCanvasConnectionFrom('bridge-map', node(), port('output-summary'))).toBe(false);
    expect(canStartCanvasConnectionFrom('bridge-map', node(true, 'input'), port('output-property'))).toBe(true);
    expect(canStartCanvasConnectionFrom('bridge-map', node(true, 'output'), port('output-property'))).toBe(false);
  });

  it('rejects ports without an anchor index', () => {
    expect(canStartCanvasConnectionFrom('edit', node(), port('output-property', null))).toBe(false);
  });
});
