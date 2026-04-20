import { NodeModel, Point, Side } from './node-model';
import { CanvasWrapper } from './canvas-wrapper';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';

export class ConnectionModel {
  private static readonly themeByStatus = {
    default: {
      connection: '#2d4bdd',
      connectionSoft: 'rgba(45, 75, 221, 0.78)',
    },
    Completed: {
      connection: '#12b76a',
      connectionSoft: 'rgba(18, 183, 106, 0.72)',
    },
    InProgress: {
      connection: '#2d4bdd',
      connectionSoft: 'rgba(45, 75, 221, 0.78)',
    },
    Failed: {
      connection: '#d92d20',
      connectionSoft: 'rgba(217, 45, 32, 0.72)',
    },
    Pending: {
      connection: '#64748b',
      connectionSoft: 'rgba(100, 116, 139, 0.72)',
    },
    NotExecuted: {
      connection: '#94a3b8',
      connectionSoft: 'rgba(148, 163, 184, 0.68)',
    },
  } as const;

  constructor(
    public from: NodeModel,
    public to: NodeModel,
    public anchor: number,
    public side: Side,
    public pos: number,
    public id: string = crypto.randomUUID(),
  ) {}

  segments(options: PathfinderOptions, obstacles: NodeModel[] = []): { x1: number; y1: number; x2: number; y2: number }[] {
    const start = this.from.anchorPositions()[this.anchor];
    let end: Point;
    switch (this.side) {
      case 'left':
        end = { x: this.to.x, y: this.to.y + this.pos };
        break;
      case 'right':
        end = { x: this.to.x + this.to.width, y: this.to.y + this.pos };
        break;
      case 'top':
        end = { x: this.to.x + this.pos, y: this.to.y };
        break;
      default:
        end = { x: this.to.x + this.pos, y: this.to.y + this.to.height };
    }

    const arrowSize = 8;
    const tipX = Math.round(end.x / options.gridSize) * options.gridSize;
    const tipY = Math.round(end.y / options.gridSize) * options.gridSize;
    const baseX = tipX - (this.side === 'right' ? arrowSize : this.side === 'left' ? -arrowSize : 0);
    const baseY = tipY - (this.side === 'bottom' ? arrowSize : this.side === 'top' ? -arrowSize : 0);

    return orthogonalSegments(
      start.x,
      start.y,
      baseX,
      baseY,
      options,
      obstacles,
      this.side,
      this.to,
      this.from.anchorSide(this.anchor),
      this.from,
    );
  }

  draw(
    wrapper: CanvasWrapper,
    obstacles: NodeModel[] = [],
    options?: {
      selected?: boolean;
    },
  ): void {
    const theme = this.from.renderStatus
      ? ConnectionModel.themeByStatus[this.from.renderStatus]
      : ConnectionModel.themeByStatus.default;
    const selected = options?.selected ?? false;

    wrapper.ctx.save();
    wrapper.ctx.strokeStyle = selected ? '#2563eb' : theme.connectionSoft;
    wrapper.ctx.fillStyle = selected ? '#2563eb' : theme.connection;
    wrapper.ctx.lineWidth = selected ? 4 : 2.5;

    const start = this.from.anchorPositions()[this.anchor];
    let end: Point;
    switch (this.side) {
      case 'left':
        end = { x: this.to.x, y: this.to.y + this.pos };
        break;
      case 'right':
        end = { x: this.to.x + this.to.width, y: this.to.y + this.pos };
        break;
      case 'top':
        end = { x: this.to.x + this.pos, y: this.to.y };
        break;
      default:
        end = { x: this.to.x + this.pos, y: this.to.y + this.to.height };
    }

    const tipX = wrapper.snap(end.x);
    const tipY = wrapper.snap(end.y);
    const baseX =
      tipX - (this.side === 'right' ? wrapper.arrowSize : this.side === 'left' ? -wrapper.arrowSize : 0);
    const baseY =
      tipY - (this.side === 'bottom' ? wrapper.arrowSize : this.side === 'top' ? -wrapper.arrowSize : 0);

    wrapper.drawOrthogonalLine(
      start.x,
      start.y,
      baseX,
      baseY,
      obstacles,
      this.side,
      this.to,
      this.from.anchorSide(this.anchor),
      this.from,
    );

    wrapper.drawArrowHead(tipX, tipY, this.side);

    wrapper.ctx.restore();
  }

  hitTest(
    x: number,
    y: number,
    tolerance: number,
    options: PathfinderOptions,
    obstacles: NodeModel[] = [],
  ): boolean {
    return this.segments(options, obstacles).some((segment) =>
      distanceToSegment(x, y, segment.x1, segment.y1, segment.x2, segment.y2) <= tolerance,
    );
  }
}

function distanceToSegment(
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
