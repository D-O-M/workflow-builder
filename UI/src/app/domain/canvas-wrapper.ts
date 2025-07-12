import { NodeModel, Side } from './node-model';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';

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


  drawOrthogonalLine(
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    obstacles: NodeModel[] = [],
    side?: Side,
    destNode?: NodeModel,
    startSide?: Side,
    startNode?: NodeModel,
  ): void {
    const options: PathfinderOptions = {
      gridSize: this.gridSize,
      nodeMargin: this.nodeMargin,
      obstacleBuffer: this.obstacleBuffer,
    };
    const segments = orthogonalSegments(
      sx,
      sy,
      ex,
      ey,
      options,
      obstacles,
      side,
      destNode,
      startSide,
      startNode,
    );
    for (const seg of segments) {
      this.drawSegment(seg.x1, seg.y1, seg.x2, seg.y2);
    }
  }
}
