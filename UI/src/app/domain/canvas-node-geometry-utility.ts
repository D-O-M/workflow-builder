import type { NodeAnchor, NodeLayout, NodePortDefinition, Point, Rect, Side } from './node-model';

export interface CanvasNodeGeometryRect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

export function clampNumber(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

export function rectContainsPoint(rect: CanvasNodeGeometryRect, x: number, y: number): boolean {
  return (
    x >= rect.x &&
    x <= rect.x + rect.width &&
    y >= rect.y &&
    y <= rect.y + rect.height
  );
}

export function rectContainsBufferedPoint(rect: CanvasNodeGeometryRect, x: number, y: number, buffer: number): boolean {
  return (
    x >= rect.x - buffer &&
    x <= rect.x + rect.width + buffer &&
    y >= rect.y - buffer &&
    y <= rect.y + rect.height + buffer
  );
}

export function rectIsFullyWithin(
  rect: CanvasNodeGeometryRect,
  left: number,
  top: number,
  right: number,
  bottom: number,
): boolean {
  return (
    rect.x >= left &&
    rect.y >= top &&
    rect.x + rect.width <= right &&
    rect.y + rect.height <= bottom
  );
}

export function sectionKey(side: 'left' | 'right', channelName: string): string {
  return `${side}:${channelName}`;
}

export function pointInsideExpandedRect(x: number, y: number, rect: Rect | undefined, padding: number): boolean {
  return !!rect &&
    x >= rect.x - padding &&
    x <= rect.x + rect.width + padding &&
    y >= rect.y - padding &&
    y <= rect.y + rect.height + padding;
}

export function inflateRect(rect: Rect, paddingX: number, paddingY: number): Rect {
  return {
    x: rect.x - paddingX,
    y: rect.y - paddingY,
    width: rect.width + paddingX * 2,
    height: rect.height + paddingY * 2,
  };
}

export function summarySocketRect(
  nodeX: number,
  nodeWidth: number,
  side: 'left' | 'right',
  centerY: number,
  size: number,
): Rect {
  const edgeX = side === 'left'
    ? nodeX
    : nodeX + nodeWidth;
  return {
    x: edgeX - size / 2,
    y: centerY - size / 2,
    width: size,
    height: size,
  };
}

export function anchorDefinitionsFromLayout(layout: NodeLayout): NodeAnchor[] {
  const anchors: NodeAnchor[] = [];
  let index = 0;

  if (layout.inputSection) {
    anchors.push({
      index,
      kind: 'input',
      label: layout.inputSection.label,
      side: 'left',
      x: layout.inputSection.summaryPort.x,
      y: layout.inputSection.summaryPort.y,
    });
    index += 1;
  }

  for (const section of layout.outputSections) {
    anchors.push({
      index,
      kind: 'output',
      label: section.label,
      side: 'right',
      x: section.summaryPort.x,
      y: section.summaryPort.y,
    });
    index += 1;
  }

  return anchors;
}

export function visiblePortsFromLayout(layout: NodeLayout): NodePortDefinition[] {
  return [...layout.summaryPorts, ...layout.propertyPorts];
}

export function portHit(
  ports: readonly NodePortDefinition[],
  x: number,
  y: number,
  extraHitRadius = 5,
): NodePortDefinition | null {
  for (const port of [...ports].reverse()) {
    if (pointInsideExpandedRect(x, y, port.hitBounds, extraHitRadius)) {
      return port;
    }

    if (Math.hypot(x - port.x, y - port.y) <= port.radius + extraHitRadius) {
      return port;
    }
  }

  return null;
}

export function sideFromNodePoint(node: CanvasNodeGeometryRect, x: number, y: number): Side {
  const cx = node.x + node.width / 2;
  const cy = node.y + node.height / 2;
  const dx = x - cx;
  const dy = y - cy;
  if (Math.abs(dx) * node.height > Math.abs(dy) * node.width) {
    return dx < 0 ? 'left' : 'right';
  }

  return dy < 0 ? 'top' : 'bottom';
}

export function snapPointToNearestSide(
  node: CanvasNodeGeometryRect,
  x: number,
  y: number,
): { x: number; y: number; side: Side; pos: number } {
  const left = Math.abs(x - node.x);
  const right = Math.abs(x - (node.x + node.width));
  const top = Math.abs(y - node.y);
  const bottom = Math.abs(y - (node.y + node.height));
  const min = Math.min(left, right, top, bottom);
  if (min === left) {
    const yy = clampNumber(y, node.y, node.y + node.height);
    return { x: node.x, y: yy, side: 'left', pos: yy - node.y };
  } else if (min === right) {
    const yy = clampNumber(y, node.y, node.y + node.height);
    return { x: node.x + node.width, y: yy, side: 'right', pos: yy - node.y };
  } else if (min === top) {
    const xx = clampNumber(x, node.x, node.x + node.width);
    return { x: xx, y: node.y, side: 'top', pos: xx - node.x };
  } else {
    const xx = clampNumber(x, node.x, node.x + node.width);
    return { x: xx, y: node.y + node.height, side: 'bottom', pos: xx - node.x };
  }
}

export function snapPointToFixedSide(
  node: CanvasNodeGeometryRect,
  x: number,
  y: number,
  side: Side,
): { x: number; y: number; side: Side; pos: number } {
  switch (side) {
    case 'left': {
      const yy = clampNumber(y, node.y, node.y + node.height);
      return { x: node.x, y: yy, side: 'left', pos: yy - node.y };
    }
    case 'right': {
      const yy = clampNumber(y, node.y, node.y + node.height);
      return { x: node.x + node.width, y: yy, side: 'right', pos: yy - node.y };
    }
    case 'top': {
      const xx = clampNumber(x, node.x, node.x + node.width);
      return { x: xx, y: node.y, side: 'top', pos: xx - node.x };
    }
    default: {
      const xx = clampNumber(x, node.x, node.x + node.width);
      return { x: xx, y: node.y + node.height, side: 'bottom', pos: xx - node.x };
    }
  }
}
