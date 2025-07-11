import { NodeModel, Side } from './node-model';

export class CanvasWrapper {
  readonly arrowSize = 8;
  constructor(
    public readonly ctx: CanvasRenderingContext2D,
    private readonly gridSize: number,
    private readonly nodeMargin: number,
    private readonly obstacleBuffer = 1,
  ) {}

  clear(width: number, height: number): void {
    this.ctx.clearRect(0, 0, width, height);
    this.ctx.strokeStyle = 'blue';
    this.ctx.fillStyle = 'blue';
    this.ctx.lineWidth = 2;
  }

  snap(value: number): number {
    return Math.round(value / this.gridSize) * this.gridSize;
  }

  drawWrappedText(text: string, x: number, y: number, maxWidth: number): void {
    const words = text.split(/\s+/);
    let line = '';
    const lineHeight = 16;
    for (const word of words) {
      const test = line ? `${line} ${word}` : word;
      if (this.ctx.measureText(test).width > maxWidth && line) {
        this.ctx.fillText(line, x, y);
        line = word;
        y += lineHeight;
      } else {
        line = test;
      }
    }
    this.ctx.fillText(line, x, y);
  }

  drawArrowHead(x: number, y: number, side: Side): void {
    const size = this.arrowSize;
    x = this.snap(x);
    y = this.snap(y);
    this.ctx.beginPath();
    switch (side) {
      case 'left':
        this.ctx.moveTo(x, y);
        this.ctx.lineTo(x - size, y - size / 2);
        this.ctx.lineTo(x - size, y + size / 2);
        break;
      case 'right':
        this.ctx.moveTo(x, y);
        this.ctx.lineTo(x + size, y - size / 2);
        this.ctx.lineTo(x + size, y + size / 2);
        break;
      case 'top':
        this.ctx.moveTo(x, y);
        this.ctx.lineTo(x - size / 2, y - size);
        this.ctx.lineTo(x + size / 2, y - size);
        break;
      default:
        this.ctx.moveTo(x, y);
        this.ctx.lineTo(x - size / 2, y + size);
        this.ctx.lineTo(x + size / 2, y + size);
    }
    this.ctx.closePath();
    this.ctx.fill();
  }

  private drawSegment(x1: number, y1: number, x2: number, y2: number): void {
    const t = this.ctx.lineWidth;
    if (x1 === x2) {
      const top = Math.min(y1, y2);
      this.ctx.fillRect(x1 - t / 2, top, t, Math.abs(y2 - y1));
    } else if (y1 === y2) {
      const left = Math.min(x1, x2);
      this.ctx.fillRect(left, y1 - t / 2, Math.abs(x2 - x1), t);
    }
  }

  private findPath(
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    obstacles: NodeModel[] = [],
    destNode?: NodeModel,
    startDir: Side = 'right',
  ): { x: number; y: number }[] {
    const grid = this.gridSize;
    const manhattan = (x1: number, y1: number, x2: number, y2: number) =>
      (Math.abs(x1 - x2) + Math.abs(y1 - y2)) / grid;

    const start = { x: this.snap(sx), y: this.snap(sy) };
    const end = { x: this.snap(ex), y: this.snap(ey) };

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

    const buffer = this.obstacleBuffer * grid;
    const blocked = (x: number, y: number): boolean => {
      const check = (n: NodeModel) =>
        x >= this.snap(n.x - buffer) &&
        x <= this.snap(n.x + n.width + buffer) &&
        y >= this.snap(n.y - buffer) &&
        y <= this.snap(n.y + n.height + buffer);
      if (destNode && check(destNode)) {
        return x !== end.x || y !== end.y;
      }
      return obstacles.some(check);
    };

    const turnPenalty = 0.1;

    while (open.length) {
      open.sort((a, b) => a.f - b.f);
      const current = open.shift()!;

      if (current.x === end.x && current.y === end.y) {
        const path: { x: number; y: number }[] = [];
        let c: Node | undefined = current;
        while (c) {
          path.push({ x: c.x, y: c.y });
          c = c.parent;
        }
        return path.reverse();
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
    }

    return [start, end];
  }

  drawOrthogonalLine(
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    obstacles: NodeModel[] = [],
    side?: Side,
    destNode?: NodeModel,
    startSide?: Side,
  ): void {
    const snapX = this.snap(ex);
    const snapY = this.snap(ey);

    const pad = this.nodeMargin;

    let curX = sx;
    let curY = sy;

    const lineTo = (x: number, y: number) => {
      if (curX === x && curY === y) return;
      this.drawSegment(curX, curY, x, y);
      curX = x;
      curY = y;
    };

    if (startSide === 'left') {
      curX -= pad;
    } else if (startSide === 'right') {
      curX += pad;
    } else if (startSide === 'top') {
      curY -= pad;
    } else if (startSide === 'bottom') {
      curY += pad;
    }

    if (curX !== sx || curY !== sy) {
      lineTo(curX, curY);
    }

    const path = this.findPath(
      curX,
      curY,
      snapX,
      snapY,
      obstacles,
      destNode,
      startSide,
    );
    for (let i = 1; i < path.length; i++) {
      lineTo(path[i].x, path[i].y);
    }

    lineTo(snapX, snapY);
    lineTo(ex, ey);
  }
}
