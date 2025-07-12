import { NodeModel, Point, Side } from './node-model';
import { CanvasWrapper } from './canvas-wrapper';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';

export class ConnectionModel {
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
      NodeModel.anchorSide(this.anchor),
      this.from,
    );
  }

  draw(wrapper: CanvasWrapper, obstacles: NodeModel[] = []): void {
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
      NodeModel.anchorSide(this.anchor),
      this.from,
    );

    wrapper.drawArrowHead(tipX, tipY, this.side);
  }
}
