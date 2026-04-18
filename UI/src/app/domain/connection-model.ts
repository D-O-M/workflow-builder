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

  draw(wrapper: CanvasWrapper, obstacles: NodeModel[] = []): void {
    const theme = this.from.renderStatus
      ? ConnectionModel.themeByStatus[this.from.renderStatus]
      : ConnectionModel.themeByStatus.default;

    wrapper.ctx.save();
    wrapper.ctx.strokeStyle = theme.connectionSoft;
    wrapper.ctx.fillStyle = theme.connection;
    wrapper.ctx.lineWidth = 2.5;

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
}
