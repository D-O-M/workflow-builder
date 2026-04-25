import { describe, it, expect } from 'vitest';
import { NodeModel } from './node-model';
import { PathfinderOptions, Segment, orthogonalRoute, orthogonalSegments } from './pathfinder';

const opts: PathfinderOptions = { gridSize: 10, nodeMargin: 20 };

function createNode(
  id: number,
  name: string,
  x: number,
  y: number,
  options?: {
    width?: number;
    height?: number;
    inputChannelName?: string | null;
    outputChannelNames?: string[];
  },
): NodeModel {
  return new NodeModel(
    id,
    id,
    0,
    0,
    id * 10,
    '0.0.1',
    name,
    x,
    y,
    options?.width ?? 320,
    options?.height ?? 220,
    options?.inputChannelName ?? null,
    options?.outputChannelNames ?? [],
    false,
  );
}

function expectCardinalSegments(segments: readonly Segment[]): void {
  expect(segments.length).toBeGreaterThan(0);
  for (const segment of segments) {
    expect(segment.x1 === segment.x2 || segment.y1 === segment.y2).toBe(true);
  }
}

function turnCount(segments: readonly Segment[]): number {
  let turns = 0;
  let previousAxis: 'horizontal' | 'vertical' | null = null;

  for (const segment of segments) {
    const axis = segment.y1 === segment.y2 ? 'horizontal' : 'vertical';
    if (previousAxis && previousAxis !== axis) {
      turns += 1;
    }

    previousAxis = axis;
  }

  return turns;
}

function routePoints(segments: readonly Segment[]): { x: number; y: number }[] {
  if (segments.length === 0) {
    return [];
  }

  return [
    { x: segments[0]!.x1, y: segments[0]!.y1 },
    ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
  ];
}

function hasImmediateBacktracking(segments: readonly Segment[]): boolean {
  const points = routePoints(segments);
  return points.some((point, index) =>
    index >= 2 &&
    point.x === points[index - 2]!.x &&
    point.y === points[index - 2]!.y);
}

function hasRepeatedPoint(segments: readonly Segment[]): boolean {
  const seen = new Set<string>();
  for (const point of routePoints(segments)) {
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

function segmentIntersectsBufferedNode(segment: Segment, node: NodeModel, margin: number): boolean {
  const left = node.x - margin;
  const right = node.x + node.width + margin;
  const top = node.y - margin;
  const bottom = node.y + node.height + margin;

  if (segment.y1 === segment.y2) {
    const y = segment.y1;
    if (y <= top || y >= bottom) {
      return false;
    }

    const start = Math.min(segment.x1, segment.x2);
    const end = Math.max(segment.x1, segment.x2);
    return Math.max(start, left) < Math.min(end, right);
  }

  if (segment.x1 === segment.x2) {
    const x = segment.x1;
    if (x <= left || x >= right) {
      return false;
    }

    const start = Math.min(segment.y1, segment.y2);
    const end = Math.max(segment.y1, segment.y2);
    return Math.max(start, top) < Math.min(end, bottom);
  }

  return true;
}

describe('pathfinder', () => {
  it('returns cardinal-only routes', () => {
    const obstacle = createNode(1, 'Obstacle', 80, -20, { width: 40, height: 40 });
    const route = orthogonalRoute(0, 0, 180, 0, opts, [obstacle]);

    expect(route.status).toBe('clear');
    expectCardinalSegments(route.segments);
  });

  it('extends source anchors by the configured node margin in the source side direction', () => {
    const source = createNode(1, 'Source', 0, 0, { outputChannelNames: ['Success'] });
    const anchor = source.outputAnchors()[0]!;
    const route = orthogonalRoute(
      anchor.x,
      anchor.y,
      anchor.x + 160,
      anchor.y,
      opts,
      [],
      undefined,
      undefined,
      'right',
      source,
    );

    expect(route.segments[0]).toEqual({
      x1: anchor.x,
      y1: anchor.y,
      x2: anchor.x + opts.nodeMargin,
      y2: anchor.y,
    });
    expectCardinalSegments(route.segments);
  });

  it('approaches destination anchors from exactly one node margin outside the destination side', () => {
    const source = createNode(1, 'Source', 0, 0, { outputChannelNames: ['Success'] });
    const destination = createNode(2, 'Destination', 460, 20, { inputChannelName: 'Input' });
    const sourceAnchor = source.outputAnchors()[0]!;
    const destinationAnchor = destination.inputAnchorSnap()!;
    const route = orthogonalRoute(
      sourceAnchor.x,
      sourceAnchor.y,
      destinationAnchor.x,
      destinationAnchor.y,
      opts,
      [],
      destinationAnchor.side,
      destination,
      'right',
      source,
    );
    const last = route.segments[route.segments.length - 1]!;

    expect(last).toEqual({
      x1: destinationAnchor.x - opts.nodeMargin,
      y1: destinationAnchor.y,
      x2: destinationAnchor.x,
      y2: destinationAnchor.y,
    });
    expectCardinalSegments(route.segments);
  });

  it('keeps clear routes outside buffered obstacle rectangles', () => {
    const obstacle = createNode(1, 'Obstacle', 80, -20, { width: 40, height: 40 });
    const route = orthogonalRoute(0, 0, 180, 0, opts, [obstacle]);

    expect(route.status).toBe('clear');
    expect(route.problemSegments).toEqual([]);
    expect(route.segments.some((segment) =>
      segmentIntersectsBufferedNode(segment, obstacle, opts.nodeMargin),
    )).toBe(false);
  });

  it('keeps clear detours to the fewest practical turn count', () => {
    const obstacle = createNode(1, 'Obstacle', 80, -20, { width: 40, height: 40 });
    const route = orthogonalRoute(0, 0, 180, 0, opts, [obstacle]);

    expect(route.status).toBe('clear');
    expect(turnCount(route.segments)).toBe(2);
  });

  it('normalizes clear routes without immediate backtracking or loopbacks', () => {
    const upper = createNode(1, 'Upper obstacle', 80, -40, { width: 50, height: 60 });
    const lower = createNode(2, 'Lower obstacle', 170, -10, { width: 50, height: 60 });
    const route = orthogonalRoute(0, 0, 300, 0, opts, [upper, lower]);

    expect(route.status).toBe('clear');
    expect(hasImmediateBacktracking(route.segments)).toBe(false);
    expect(hasRepeatedPoint(route.segments)).toBe(false);
    expectCardinalSegments(route.segments);
  });

  it('avoids double-backing immediately after source or before target anchors', () => {
    const route = orthogonalRoute(520, 100, 320, 100, opts, [], 'left', undefined, 'right');

    expect(route.status).toBe('clear');
    expect(route.segments[0]).toEqual({ x1: 520, y1: 100, x2: 540, y2: 100 });
    expect(route.segments[route.segments.length - 1]).toEqual({ x1: 300, y1: 100, x2: 320, y2: 100 });
    expect(hasOppositeConsecutiveSegments(route.segments)).toBe(false);
    expect(hasRepeatedPoint(route.segments)).toBe(false);
  });

  it('prefers a few-bend route over a shorter but bendier corridor', () => {
    const first = createNode(1, 'First blocker', 70, -20, { width: 50, height: 40 });
    const second = createNode(2, 'Second blocker', 170, 10, { width: 50, height: 40 });
    const route = orthogonalRoute(0, 0, 320, 0, opts, [first, second]);

    expect(route.status).toBe('clear');
    expect(turnCount(route.segments)).toBeLessThanOrEqual(2);
  });

  it('returns a simple obstruction route instead of an excessive clear loop around a wall', () => {
    const wall = createNode(1, 'Wall', 40, -1000, { width: 40, height: 2000 });
    const route = orthogonalRoute(0, 0, 140, 0, opts, [wall]);

    expect(route.status).toBe('obstacleViolation');
    expect(route.problemSegments.length).toBeGreaterThan(0);
    expect(turnCount(route.segments)).toBeLessThanOrEqual(1);
    expect(hasRepeatedPoint(route.segments)).toBe(false);
  });

  it('returns an obstacle violation with problem subsegments when no clear buffered route exists', () => {
    const blocker = createNode(1, 'Blocker', -10, -10, { width: 40, height: 20 });
    const route = orthogonalRoute(0, 0, 120, 0, opts, [blocker]);

    expect(route.status).toBe('obstacleViolation');
    expect(route.validationMessage).toContain('reserved routing buffer');
    expect(route.problemSegments.length).toBeGreaterThan(0);
    expectCardinalSegments(route.segments);
    expectCardinalSegments(route.problemSegments);
  });

  it('does not report an unreachable partial A* route as clear', () => {
    const blocker = createNode(1, 'Blocker', -30, -30, { width: 180, height: 60 });
    const route = orthogonalRoute(0, 0, 120, 0, opts, [blocker]);

    expect(route.status).toBe('obstacleViolation');
    expect(route.problemSegments.length).toBeGreaterThan(0);
  });

  it('keeps the legacy plain segment surface available', () => {
    const segments = orthogonalSegments(0, 0, 40, 0, opts);

    expect(segments).toEqual([{ x1: 0, y1: 0, x2: 40, y2: 0 }]);
  });
});
