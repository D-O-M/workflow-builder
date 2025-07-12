import { NodeModel, Side } from './node-model';

export interface PathfinderOptions {
  gridSize: number;
  nodeMargin: number;
  obstacleBuffer?: number;
}

export interface Point { x: number; y: number }
export interface Segment { x1: number; y1: number; x2: number; y2: number }

function snap(value: number, grid: number): number {
  return Math.round(value / grid) * grid;
}

export function findPath(
  sx: number,
  sy: number,
  ex: number,
  ey: number,
  opts: PathfinderOptions,
  obstacles: NodeModel[] = [],
  destNode?: NodeModel,
  startDir: Side = 'right',
): Point[] {
  const grid = opts.gridSize;
  const manhattan = (x1: number, y1: number, x2: number, y2: number) =>
    (Math.abs(x1 - x2) + Math.abs(y1 - y2)) / grid;

  const start = { x: snap(sx, grid), y: snap(sy, grid) };
  const end = { x: snap(ex, grid), y: snap(ey, grid) };

  const margin = grid * 10;
  const minX = Math.min(start.x, end.x) - margin;
  const maxX = Math.max(start.x, end.x) + margin;
  const minY = Math.min(start.y, end.y) - margin;
  const maxY = Math.max(start.y, end.y) + margin;

  type Node = {
    x: number;
    y: number;
    dir: Side;
    g: number;
    f: number;
    parent?: Node;
  };

  const key = (x: number, y: number, d: Side) => `${x},${y},${d}`;

  const open: Node[] = [];
  const map = new Map<string, Node>();
  const startNode: Node = {
    x: start.x,
    y: start.y,
    dir: startDir,
    g: 0,
    f: manhattan(start.x, start.y, end.x, end.y),
  };
  open.push(startNode);
  map.set(key(start.x, start.y, startDir), startNode);

  const dirs: { side: Side; dx: number; dy: number }[] = [
    { side: 'left', dx: -grid, dy: 0 },
    { side: 'right', dx: grid, dy: 0 },
    { side: 'top', dx: 0, dy: -grid },
    { side: 'bottom', dx: 0, dy: grid },
  ];

  const buffer = (opts.obstacleBuffer ?? 1) * grid;
  const blocked = (x: number, y: number): boolean => {
    const check = (n: NodeModel) =>
      x >= snap(n.x - buffer, grid) &&
      x <= snap(n.x + n.width + buffer, grid) &&
      y >= snap(n.y - buffer, grid) &&
      y <= snap(n.y + n.height + buffer, grid);
    if (destNode) {
      const insideDest =
        x >= snap(destNode.x, grid) &&
        x <= snap(destNode.x + destNode.width, grid) &&
        y >= snap(destNode.y, grid) &&
        y <= snap(destNode.y + destNode.height, grid);
      if (insideDest) {
        return x !== end.x || y !== end.y;
      }
    }
    return obstacles.some(check);
  };

  const turnPenalty = 0.1;

  const startTime = performance.now();
  const timeLimit = 20; // ms
  let best = startNode;

  while (open.length) {
    open.sort((a, b) => a.f - b.f);
    const current = open.shift()!;

    if (current.f < best.f) {
      best = current;
    }

    if (current.x === end.x && current.y === end.y) {
      best = current;
      break;
    }

    for (const d of dirs) {
      const nx = current.x + d.dx;
      const ny = current.y + d.dy;

      if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
      if (blocked(nx, ny)) continue;

      const cost = current.g + 1 + (d.side === current.dir ? 0 : turnPenalty);
      const k = key(nx, ny, d.side);
      const existing = map.get(k);
      if (!existing || cost < existing.g) {
        const node: Node = {
          x: nx,
          y: ny,
          dir: d.side,
          g: cost,
          f: cost + manhattan(nx, ny, end.x, end.y),
          parent: current,
        };
        map.set(k, node);
        open.push(node);
      }
    }

    if (performance.now() - startTime > timeLimit) {
      break;
    }
  }

  const path: Point[] = [];
  let c: Node | undefined = best;
  while (c) {
    path.push({ x: c.x, y: c.y });
    c = c.parent;
  }
  return path.length ? path.reverse() : [start, end];
}

export function orthogonalSegments(
  sx: number,
  sy: number,
  ex: number,
  ey: number,
  opts: PathfinderOptions,
  obstacles: NodeModel[] = [],
  side?: Side,
  destNode?: NodeModel,
  startSide?: Side,
  startNode?: NodeModel,
): Segment[] {
  const grid = opts.gridSize;
  const pad = opts.nodeMargin;

  let dx = ex;
  let dy = ey;
  if (
    destNode &&
    side &&
    destNode.containsBuffered(ex, ey, pad) &&
    !destNode.contains(ex, ey)
  ) {
    const snapPoint = destNode.snapPointFixedSide(ex, ey, side);
    dx = snapPoint.x;
    dy = snapPoint.y;
  }

  const snapX = snap(dx, grid);
  const snapY = snap(dy, grid);

  let curX = sx;
  let curY = sy;

  const segments: Segment[] = [];
  const lineTo = (x: number, y: number) => {
    if (curX === x && curY === y) return;
    segments.push({ x1: curX, y1: curY, x2: x, y2: y });
    curX = x;
    curY = y;
  };

  const inStartBuffer = startNode?.containsBuffered(dx, dy, pad) ?? false;

  if (startSide && !inStartBuffer) {
    let nx = curX;
    let ny = curY;
    if (startSide === 'left') {
      nx -= pad;
    } else if (startSide === 'right') {
      nx += pad;
    } else if (startSide === 'top') {
      ny -= pad;
    } else if (startSide === 'bottom') {
      ny += pad;
    }
    if (nx !== curX || ny !== curY) {
      lineTo(nx, ny);
    }
  }

  if (inStartBuffer) {
    lineTo(dx, dy);
    return segments;
  }

  let targetX = snapX;
  let targetY = snapY;
  let searchObstacles = obstacles;

  if (destNode && side) {
    const margin = opts.nodeMargin;
    switch (side) {
      case 'left':
        targetX = snap(destNode.x - margin, grid);
        targetY = snapY;
        break;
      case 'right':
        targetX = snap(destNode.x + destNode.width + margin, grid);
        targetY = snapY;
        break;
      case 'top':
        targetX = snapX;
        targetY = snap(destNode.y - margin, grid);
        break;
      case 'bottom':
        targetX = snapX;
        targetY = snap(destNode.y + destNode.height + margin, grid);
        break;
    }
    searchObstacles = [...obstacles, destNode];
  }

  const path = findPath(
    curX,
    curY,
    targetX,
    targetY,
    opts,
    searchObstacles,
    destNode,
    startSide,
  );
  for (let i = 1; i < path.length; i++) {
    lineTo(path[i].x, path[i].y);
  }

  lineTo(targetX, targetY);
  if (destNode) {
    lineTo(snapX, snapY);
  }
  lineTo(dx, dy);

  return segments;
}
