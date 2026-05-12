import { NodePortDefinition, Point, Rect, Side } from './node-model';
import { RouteProblemSegment, Segment } from './pathfinder';
import { CanvasBounds, RenderedConnectionRoute } from './canvas-render-types';
import type { RenderedNode } from './canvas-render-node-model';

export function createLaneAdjustedSegments(
  segments: readonly Segment[],
  laneOffset: number,
): readonly Segment[] {
  if (segments.length < 3 || segments.length > 7 || laneOffset === 0) {
    return segments;
  }

  const first = segments[0]!;
  const last = segments[segments.length - 1]!;
  const middleSegments = segments.slice(1, -1);
  if (middleSegments.length === 0) {
    return segments;
  }

  const shiftedMiddlePoints = createOffsetCorridorPoints(middleSegments, laneOffset);
  if (shiftedMiddlePoints.length < 2) {
    return segments;
  }

  const adjusted: Segment[] = [{ ...first }];
  appendSegmentsBetweenPoints(adjusted, { x: first.x2, y: first.y2 }, shiftedMiddlePoints[0]!);
  for (let index = 0; index < shiftedMiddlePoints.length - 1; index += 1) {
    appendSegmentsBetweenPoints(adjusted, shiftedMiddlePoints[index]!, shiftedMiddlePoints[index + 1]!);
  }
  appendSegmentsBetweenPoints(adjusted, shiftedMiddlePoints[shiftedMiddlePoints.length - 1]!, { x: last.x1, y: last.y1 });
  adjusted.push({ ...last });

  return laneAdjustmentIsSafe(segments, adjusted) ? adjusted : segments;
}

export function createLaneAdjustedProblemSegments(
  problemSegments: readonly RouteProblemSegment[],
  canonicalSegments: readonly Segment[],
  laneOffset: number,
): readonly RouteProblemSegment[] {
  if (problemSegments.length === 0 || laneOffset === 0) {
    return problemSegments;
  }

  const firstSegment = canonicalSegments[0] ?? null;
  const lastSegment = canonicalSegments[canonicalSegments.length - 1] ?? null;
  return problemSegments.map((segment) => {
    if ((firstSegment && isSameAxisSubSegment(segment, firstSegment)) ||
      (lastSegment && isSameAxisSubSegment(segment, lastSegment))) {
      return segment;
    }

    return {
      ...segment,
      ...offsetSegmentPerpendicular(segment, laneOffset),
    };
  });
}

function createOffsetCorridorPoints(segments: readonly Segment[], laneOffset: number): Point[] {
  const shiftedSegments = segments.map((segment) => offsetSegmentPerpendicular(segment, laneOffset));
  const points: Point[] = [{ x: shiftedSegments[0]!.x1, y: shiftedSegments[0]!.y1 }];

  for (let index = 0; index < shiftedSegments.length - 1; index += 1) {
    const current = shiftedSegments[index]!;
    const next = shiftedSegments[index + 1]!;
    const intersection = orthogonalLineIntersection(current, next);
    points.push(intersection ?? { x: current.x2, y: current.y2 });
  }

  const last = shiftedSegments[shiftedSegments.length - 1]!;
  points.push({ x: last.x2, y: last.y2 });
  return removeConsecutiveDuplicatePoints(points);
}

function orthogonalLineIntersection(first: Segment, second: Segment): Point | null {
  const firstHorizontal = first.y1 === first.y2;
  const secondHorizontal = second.y1 === second.y2;
  if (firstHorizontal === secondHorizontal) {
    return null;
  }

  return firstHorizontal
    ? { x: second.x1, y: first.y1 }
    : { x: first.x1, y: second.y1 };
}

function appendSegmentsBetweenPoints(segments: Segment[], start: Point, end: Point): void {
  if (start.x === end.x || start.y === end.y) {
    appendSegment(segments, { x1: start.x, y1: start.y, x2: end.x, y2: end.y });
    return;
  }

  const elbow = { x: end.x, y: start.y };
  appendSegment(segments, { x1: start.x, y1: start.y, x2: elbow.x, y2: elbow.y });
  appendSegment(segments, { x1: elbow.x, y1: elbow.y, x2: end.x, y2: end.y });
}

function laneAdjustmentIsSafe(canonicalSegments: readonly Segment[], visualSegments: readonly Segment[]): boolean {
  const canonicalFirst = canonicalSegments[0];
  const canonicalLast = canonicalSegments[canonicalSegments.length - 1];
  const visualFirst = visualSegments[0];
  const visualLast = visualSegments[visualSegments.length - 1];
  if (!canonicalFirst || !canonicalLast || !visualFirst || !visualLast) {
    return false;
  }

  if (!segmentsShareEndpoints(canonicalFirst, visualFirst) || !segmentsShareEndpoints(canonicalLast, visualLast)) {
    return false;
  }

  if (!visualSegments.every(isCardinalSegment)) {
    return false;
  }

  const points = routeSegmentsToPoints(visualSegments);
  if (hasImmediateBacktrack(points) || hasRepeatedPoint(points) || hasOppositeConsecutiveSegments(visualSegments)) {
    return false;
  }

  return routeTurnCount(visualSegments) <= routeTurnCount(canonicalSegments) + 4;
}

function segmentsShareEndpoints(left: Segment, right: Segment): boolean {
  return left.x1 === right.x1 && left.y1 === right.y1 && left.x2 === right.x2 && left.y2 === right.y2;
}

function isCardinalSegment(segment: Segment): boolean {
  return segment.x1 === segment.x2 || segment.y1 === segment.y2;
}

function routeTurnCount(segments: readonly Segment[]): number {
  let turns = 0;
  let previousAxis: 'horizontal' | 'vertical' | null = null;

  for (const segment of segments) {
    const axis = segment.y1 === segment.y2
      ? 'horizontal'
      : segment.x1 === segment.x2
        ? 'vertical'
        : null;
    if (!axis) {
      continue;
    }

    if (previousAxis && previousAxis !== axis) {
      turns += 1;
    }

    previousAxis = axis;
  }

  return turns;
}

function hasImmediateBacktrack(points: readonly Point[]): boolean {
  for (let index = 2; index < points.length; index += 1) {
    if (points[index]!.x === points[index - 2]!.x && points[index]!.y === points[index - 2]!.y) {
      return true;
    }
  }

  return false;
}

function hasRepeatedPoint(points: readonly Point[]): boolean {
  const seen = new Set<string>();
  for (const point of points) {
    const key = `${point.x}:${point.y}`;
    if (seen.has(key)) {
      return true;
    }

    seen.add(key);
  }

  return false;
}

function hasOppositeConsecutiveSegments(segments: readonly Segment[]): boolean {
  for (let index = 1; index < segments.length; index += 1) {
    const previous = segments[index - 1]!;
    const current = segments[index]!;
    if (previous.y1 === previous.y2 && current.y1 === current.y2 && previous.y1 === current.y1) {
      const previousDirection = Math.sign(previous.x2 - previous.x1);
      const currentDirection = Math.sign(current.x2 - current.x1);
      if (previousDirection !== 0 && currentDirection !== 0 && previousDirection === -currentDirection) {
        return true;
      }
    }

    if (previous.x1 === previous.x2 && current.x1 === current.x2 && previous.x1 === current.x1) {
      const previousDirection = Math.sign(previous.y2 - previous.y1);
      const currentDirection = Math.sign(current.y2 - current.y1);
      if (previousDirection !== 0 && currentDirection !== 0 && previousDirection === -currentDirection) {
        return true;
      }
    }
  }

  return false;
}

function removeConsecutiveDuplicatePoints(points: readonly Point[]): Point[] {
  const result: Point[] = [];
  for (const point of points) {
    const previous = result[result.length - 1];
    if (previous && previous.x === point.x && previous.y === point.y) {
      continue;
    }

    result.push(point);
  }

  return result;
}

function routeSegmentsToPoints(segments: readonly Segment[]): Point[] {
  if (segments.length === 0) {
    return [];
  }

  return [
    { x: segments[0]!.x1, y: segments[0]!.y1 },
    ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
  ];
}

function appendSegment(segments: Segment[], segment: Segment): void {
  if (segment.x1 === segment.x2 && segment.y1 === segment.y2) {
    return;
  }

  const previous = segments[segments.length - 1];
  if (previous && previous.x2 === segment.x1 && previous.y2 === segment.y1) {
    if (previous.y1 === previous.y2 && segment.y1 === segment.y2) {
      previous.x2 = segment.x2;
      return;
    }

    if (previous.x1 === previous.x2 && segment.x1 === segment.x2) {
      previous.y2 = segment.y2;
      return;
    }
  }

  segments.push({ ...segment });
}

function offsetSegmentPerpendicular(segment: Segment, laneOffset: number): Segment {
  if (segment.y1 === segment.y2) {
    return {
      x1: segment.x1,
      y1: segment.y1 + laneOffset,
      x2: segment.x2,
      y2: segment.y2 + laneOffset,
    };
  }

  return {
    x1: segment.x1 + laneOffset,
    y1: segment.y1,
    x2: segment.x2 + laneOffset,
    y2: segment.y2,
  };
}

function isSameAxisSubSegment(candidate: Segment, container: Segment): boolean {
  if (candidate.y1 === candidate.y2 && container.y1 === container.y2 && candidate.y1 === container.y1) {
    return rangesOverlap(candidate.x1, candidate.x2, container.x1, container.x2);
  }

  if (candidate.x1 === candidate.x2 && container.x1 === container.x2 && candidate.x1 === container.x1) {
    return rangesOverlap(candidate.y1, candidate.y2, container.y1, container.y2);
  }

  return false;
}

function rangesOverlap(a1: number, a2: number, b1: number, b2: number): boolean {
  const aMin = Math.min(a1, a2);
  const aMax = Math.max(a1, a2);
  const bMin = Math.min(b1, b2);
  const bMax = Math.max(b1, b2);
  return aMin >= bMin && aMax <= bMax;
}

export function portBounds(port: NodePortDefinition): CanvasBounds {
  if (port.hitBounds) {
    return rectToBounds(port.hitBounds);
  }

  return {
    left: port.x - port.radius,
    top: port.y - port.radius,
    right: port.x + port.radius,
    bottom: port.y + port.radius,
  };
}

export function portScopedKey(node: RenderedNode, port: NodePortDefinition): string {
  return `${node.id}:${port.key}`;
}

export function rectToBounds(rect: Rect): CanvasBounds {
  return {
    left: rect.x,
    top: rect.y,
    right: rect.x + rect.width,
    bottom: rect.y + rect.height,
  };
}

export function unionBounds(left: CanvasBounds, right: CanvasBounds): CanvasBounds {
  return {
    left: Math.min(left.left, right.left),
    top: Math.min(left.top, right.top),
    right: Math.max(left.right, right.right),
    bottom: Math.max(left.bottom, right.bottom),
  };
}

export function pointInsideExpandedRect(x: number, y: number, rect: Rect | undefined, padding: number): boolean {
  return !!rect &&
    x >= rect.x - padding &&
    x <= rect.x + rect.width + padding &&
    y >= rect.y - padding &&
    y <= rect.y + rect.height + padding;
}

export function unionRouteBounds(routes: readonly RenderedConnectionRoute[]): CanvasBounds | null {
  let left = Number.POSITIVE_INFINITY;
  let top = Number.POSITIVE_INFINITY;
  let right = Number.NEGATIVE_INFINITY;
  let bottom = Number.NEGATIVE_INFINITY;

  for (const route of routes) {
    if (!route.bounds) {
      continue;
    }

    left = Math.min(left, route.bounds.left);
    top = Math.min(top, route.bounds.top);
    right = Math.max(right, route.bounds.right);
    bottom = Math.max(bottom, route.bounds.bottom);
  }

  return Number.isFinite(left) ? { left, top, right, bottom } : null;
}

export function unionSegmentBounds(segments: readonly Segment[]): CanvasBounds | null {
  if (segments.length === 0) {
    return null;
  }

  let left = Number.POSITIVE_INFINITY;
  let top = Number.POSITIVE_INFINITY;
  let right = Number.NEGATIVE_INFINITY;
  let bottom = Number.NEGATIVE_INFINITY;

  for (const segment of segments) {
    left = Math.min(left, segment.x1, segment.x2);
    top = Math.min(top, segment.y1, segment.y2);
    right = Math.max(right, segment.x1, segment.x2);
    bottom = Math.max(bottom, segment.y1, segment.y2);
  }

  return { left, top, right, bottom };
}

export function strokeContinuousRoute(ctx: CanvasRenderingContext2D, segments: readonly Segment[]): void {
  if (segments.length === 0) {
    return;
  }

  const points = routeSegmentsToPoints(segments);
  if (points.length < 3) {
    strokeSegments(ctx, segments);
    return;
  }

  const cornerRadius = 7;
  ctx.beginPath();
  ctx.moveTo(points[0]!.x, points[0]!.y);

  for (let index = 1; index < points.length - 1; index += 1) {
    const previous = points[index - 1]!;
    const current = points[index]!;
    const next = points[index + 1]!;

    if (pointsAreCollinear(previous, current, next)) {
      ctx.lineTo(current.x, current.y);
      continue;
    }

    const radius = Math.min(
      cornerRadius,
      distanceBetweenPoints(previous, current) / 2,
      distanceBetweenPoints(current, next) / 2,
    );
    if (radius <= 0) {
      ctx.lineTo(current.x, current.y);
      continue;
    }

    const cornerStart = pointToward(current, previous, radius);
    const cornerEnd = pointToward(current, next, radius);
    ctx.lineTo(cornerStart.x, cornerStart.y);
    ctx.quadraticCurveTo(current.x, current.y, cornerEnd.x, cornerEnd.y);
  }

  const lastPoint = points[points.length - 1]!;
  ctx.lineTo(lastPoint.x, lastPoint.y);
  ctx.stroke();
}

export function fillRouteEndpointCaps(ctx: CanvasRenderingContext2D, route: RenderedConnectionRoute, lineWidth: number): void {
  const firstSegment = route.visualSegments[0];
  const lastSegment = route.visualSegments[route.visualSegments.length - 1];
  if (!firstSegment || !lastSegment) {
    return;
  }

  fillConnectionEndpointCap(ctx, { x: firstSegment.x1, y: firstSegment.y1 }, route.startSide, lineWidth);
  fillConnectionEndpointCap(ctx, { x: lastSegment.x2, y: lastSegment.y2 }, route.side, lineWidth);
}

function fillConnectionEndpointCap(ctx: CanvasRenderingContext2D, point: Point, side: Side, lineWidth: number): void {
  const inward = inwardVectorForSide(side);
  const perpendicular = { x: -inward.y, y: inward.x };
  const length = Math.max(8, lineWidth * 1.25);
  const halfWidth = Math.max(4, lineWidth * 0.68);
  const tip = {
    x: point.x + inward.x * length,
    y: point.y + inward.y * length,
  };

  ctx.beginPath();
  ctx.moveTo(tip.x, tip.y);
  ctx.lineTo(point.x + perpendicular.x * halfWidth, point.y + perpendicular.y * halfWidth);
  ctx.lineTo(point.x - perpendicular.x * halfWidth, point.y - perpendicular.y * halfWidth);
  ctx.closePath();
  ctx.fill();
}

function inwardVectorForSide(side: Side): Point {
  switch (side) {
    case 'left':
      return { x: 1, y: 0 };
    case 'right':
      return { x: -1, y: 0 };
    case 'top':
      return { x: 0, y: 1 };
    case 'bottom':
      return { x: 0, y: -1 };
  }
}

function pointsAreCollinear(previous: Point, current: Point, next: Point): boolean {
  return (previous.x === current.x && current.x === next.x) ||
    (previous.y === current.y && current.y === next.y);
}

function distanceBetweenPoints(start: Point, end: Point): number {
  return Math.abs(end.x - start.x) + Math.abs(end.y - start.y);
}

function pointToward(from: Point, to: Point, distance: number): Point {
  const totalDistance = distanceBetweenPoints(from, to);
  if (totalDistance === 0) {
    return { ...from };
  }

  const ratio = distance / totalDistance;
  return {
    x: from.x + (to.x - from.x) * ratio,
    y: from.y + (to.y - from.y) * ratio,
  };
}

export function strokeSegments(ctx: CanvasRenderingContext2D, segments: readonly Segment[]): void {
  for (const segment of segments) {
    ctx.beginPath();
    ctx.moveTo(segment.x1, segment.y1);
    ctx.lineTo(segment.x2, segment.y2);
    ctx.stroke();
  }
}

export function boundsContainPoint(bounds: CanvasBounds, x: number, y: number, padding = 0): boolean {
  return (
    x >= bounds.left - padding &&
    x <= bounds.right + padding &&
    y >= bounds.top - padding &&
    y <= bounds.bottom + padding
  );
}

export function boundsIntersect(left: CanvasBounds, right: CanvasBounds, padding = 0): boolean {
  return (
    left.left <= right.right + padding &&
    left.right >= right.left - padding &&
    left.top <= right.bottom + padding &&
    left.bottom >= right.top - padding
  );
}

export function inflateBounds(bounds: CanvasBounds, padding: number): CanvasBounds {
  return {
    left: bounds.left - padding,
    top: bounds.top - padding,
    right: bounds.right + padding,
    bottom: bounds.bottom + padding,
  };
}

export function addConnectedNodeId(map: Map<number, Set<number>>, sourceId: number, targetId: number): void {
  const existing = map.get(sourceId);
  if (existing) {
    existing.add(targetId);
    return;
  }

  map.set(sourceId, new Set([targetId]));
}

export function distanceToSegment(
  px: number,
  py: number,
  x1: number,
  y1: number,
  x2: number,
  y2: number,
): number {
  const dx = x2 - x1;
  const dy = y2 - y1;

  if (dx === 0 && dy === 0) {
    return Math.hypot(px - x1, py - y1);
  }

  const numerator = (px - x1) * dx + (py - y1) * dy;
  const denominator = dx * dx + dy * dy;
  const t = Math.min(1, Math.max(0, numerator / denominator));
  const closestX = x1 + dx * t;
  const closestY = y1 + dy * t;
  return Math.hypot(px - closestX, py - closestY);
}
