import { BlueprintCanvasMode, CanvasModeCapabilities } from './canvas-runtime';

interface CanvasModeNode {
  readonly base: {
    readonly isDataBridge: boolean;
    readonly dataBridgeKind: 'input' | 'output' | null;
  };
}

interface CanvasModePort {
  readonly kind: string;
  readonly anchorIndex: number | null;
}

const CANVAS_MODE_CAPABILITIES: Record<BlueprintCanvasMode, CanvasModeCapabilities> = {
  edit: {
    readOnly: false,
    graphEditable: true,
    connectionMapping: true,
    passiveSummaryPorts: false,
    dragConnectionPreview: true,
    selectableConnections: true,
  },
  readonly: {
    readOnly: true,
    graphEditable: false,
    connectionMapping: false,
    passiveSummaryPorts: false,
    dragConnectionPreview: false,
    selectableConnections: false,
  },
  'bridge-map': {
    readOnly: false,
    graphEditable: false,
    connectionMapping: true,
    passiveSummaryPorts: false,
    dragConnectionPreview: true,
    selectableConnections: false,
  },
  'bridge-source': {
    readOnly: true,
    graphEditable: false,
    connectionMapping: true,
    passiveSummaryPorts: true,
    dragConnectionPreview: false,
    selectableConnections: false,
  },
  'bridge-target': {
    readOnly: true,
    graphEditable: false,
    connectionMapping: false,
    passiveSummaryPorts: true,
    dragConnectionPreview: false,
    selectableConnections: false,
  },
};

export function canvasModeCapabilities(mode: BlueprintCanvasMode): CanvasModeCapabilities {
  return CANVAS_MODE_CAPABILITIES[mode];
}

export function isPassiveCanvasSummaryPort(mode: BlueprintCanvasMode, port: { readonly kind: string }): boolean {
  return canvasModeCapabilities(mode).passiveSummaryPorts &&
    (port.kind === 'input-summary' || port.kind === 'output-summary');
}

export function canStartCanvasConnectionFrom(
  mode: BlueprintCanvasMode,
  node: CanvasModeNode,
  port: CanvasModePort,
): boolean {
  if (!canvasModeCapabilities(mode).connectionMapping || port.anchorIndex === null) {
    return false;
  }

  if (mode === 'bridge-map') {
    if (node.base.isDataBridge) {
      return node.base.dataBridgeKind === 'input' && port.kind === 'output-property';
    }

    return port.kind === 'output-property';
  }

  if (mode === 'bridge-source') {
    return !node.base.isDataBridge && port.kind === 'output-property';
  }

  if (node.base.isDataBridge) {
    return node.base.dataBridgeKind === 'input' &&
      (port.kind === 'output-summary' || port.kind === 'output-property');
  }

  return port.kind === 'output-summary' || port.kind === 'output-property';
}
