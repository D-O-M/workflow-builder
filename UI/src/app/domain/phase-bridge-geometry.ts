export type PhaseSplitMode = 'horizontal' | 'vertical';

export type BridgeCurveOutwardSide = 'left' | 'right';

export interface BridgeCurvePathOptions {
  startOutward?: BridgeCurveOutwardSide;
  endOutward?: BridgeCurveOutwardSide;
}

const BRIDGE_NODE_PROTRUSION = 34;

export function normalizeBridgePosition(value: number | null | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 0.5;
  }

  return Math.min(1, Math.max(0, value));
}

export function bridgePointFromDividerRect(
  kind: 'input' | 'output',
  bridgePosition: number,
  splitMode: PhaseSplitMode,
  rect: Pick<DOMRect, 'left' | 'right' | 'top' | 'bottom' | 'width' | 'height'>,
): { clientX: number; clientY: number } {
  if (splitMode === 'vertical') {
    return {
      clientX: kind === 'input' ? rect.left : rect.right,
      clientY: rect.top + rect.height * normalizeBridgePosition(bridgePosition),
    };
  }

  return {
    clientX: rect.left + rect.width * normalizeBridgePosition(bridgePosition),
    clientY: kind === 'input' ? rect.top : rect.bottom,
  };
}

export function createPhaseBridgeCurvePath(
  workbenchRect: Pick<DOMRect, 'left' | 'top'>,
  startClient: { clientX: number; clientY: number },
  endClient: { clientX: number; clientY: number },
  options: BridgeCurvePathOptions = {},
): string {
  const start = toWorkbenchPoint(workbenchRect, startClient);
  const end = toWorkbenchPoint(workbenchRect, endClient);
  const direction = end.y >= start.y ? 1 : -1;
  const verticalDistance = Math.abs(end.y - start.y);
  const controlOffset = Math.max(42, Math.min(220, verticalDistance * 0.62));
  const firstControl = options.startOutward
    ? protrudeBridgeCurvePoint(start, options.startOutward)
    : { x: start.x, y: start.y + controlOffset * direction };
  const secondControl = options.endOutward
    ? protrudeBridgeCurvePoint(end, options.endOutward)
    : { x: end.x, y: end.y - controlOffset * direction };

  return [
    `M ${roundPathCoordinate(start.x)} ${roundPathCoordinate(start.y)}`,
    `C ${roundPathCoordinate(firstControl.x)} ${roundPathCoordinate(firstControl.y)}`,
    `${roundPathCoordinate(secondControl.x)} ${roundPathCoordinate(secondControl.y)}`,
    `${roundPathCoordinate(end.x)} ${roundPathCoordinate(end.y)}`,
  ].join(' ');
}

function protrudeBridgeCurvePoint(
  point: { x: number; y: number },
  side: BridgeCurveOutwardSide,
): { x: number; y: number } {
  return {
    x: point.x + (side === 'right' ? BRIDGE_NODE_PROTRUSION : -BRIDGE_NODE_PROTRUSION),
    y: point.y,
  };
}

function toWorkbenchPoint(
  workbenchRect: Pick<DOMRect, 'left' | 'top'>,
  point: { clientX: number; clientY: number },
): { x: number; y: number } {
  return {
    x: point.clientX - workbenchRect.left,
    y: point.clientY - workbenchRect.top,
  };
}

function roundPathCoordinate(value: number): number {
  return Math.round(value * 10) / 10;
}
