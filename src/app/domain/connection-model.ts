import { NodeModel, Point, Side } from './node-model';
import { CanvasWrapper } from './canvas-wrapper';

export class ConnectionModel {
  constructor(
    public from: NodeModel,
    public to: NodeModel,
    public anchor: number,
    public side: Side,
    public pos: number,
  ) {}

  draw(wrapper: CanvasWrapper): void {
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

    wrapper.drawOrthogonalLine(
      start.x,
      start.y,
      end.x,
      end.y,
      this.side,
      this.to,
      NodeModel.anchorSide(this.anchor),
    );

    wrapper.drawArrowHead(end.x, end.y, this.side);
  }
}
