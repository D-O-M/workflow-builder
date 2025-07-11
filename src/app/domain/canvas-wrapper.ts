import { NodeModel, Side } from './node-model';

export class CanvasWrapper {
  constructor(
    public readonly ctx: CanvasRenderingContext2D,
    private readonly gridSize: number,
    private readonly nodeMargin: number,
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

  drawWrappedText(
    text: string,
    x: number,
    y: number,
    maxWidth: number,
  ): void {
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

  private drawArrowHead(fromX: number, fromY: number, toX: number, toY: number): void {
    const angle = Math.atan2(toY - fromY, toX - fromX);
    const size = 8;
    this.ctx.beginPath();
    this.ctx.moveTo(toX, toY);
    this.ctx.lineTo(
      toX - size * Math.cos(angle - Math.PI / 6),
      toY - size * Math.sin(angle - Math.PI / 6),
    );
    this.ctx.lineTo(
      toX - size * Math.cos(angle + Math.PI / 6),
      toY - size * Math.sin(angle + Math.PI / 6),
    );
    this.ctx.closePath();
    this.ctx.fill();
  }

  drawOrthogonalLine(
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    side?: Side,
    destNode?: NodeModel,
  ): void {
    const snapX = this.snap(ex);
    const snapY = this.snap(ey);
    this.ctx.beginPath();
    this.ctx.moveTo(sx, sy);

    const pad = this.nodeMargin;

    if (destNode) {
      const left = destNode.x - pad;
      const right = destNode.x + destNode.width + pad;
      const top = destNode.y - pad;
      const bottom = destNode.y + destNode.height + pad;

      if (side === 'left' || side === 'right') {
        const approachX = side === 'left' ? left : right;
        let curX = sx;
        let curY = sy;
        if (curY >= top && curY <= bottom) {
          const escapeY = curY < destNode.y + destNode.height / 2 ? top : bottom;
          this.ctx.lineTo(curX, escapeY);
          curY = escapeY;
        }
        if (curX !== approachX) {
          this.ctx.lineTo(approachX, curY);
          curX = approachX;
        }
        if (curY !== snapY) {
          this.ctx.lineTo(curX, snapY);
        }
      } else if (side === 'top' || side === 'bottom') {
        const approachY = side === 'top' ? top : bottom;
        let curX = sx;
        let curY = sy;
        if (curX >= left && curX <= right) {
          const escapeX = curX < destNode.x + destNode.width / 2 ? left : right;
          this.ctx.lineTo(escapeX, curY);
          curX = escapeX;
        }
        if (curY !== approachY) {
          this.ctx.lineTo(curX, approachY);
          curY = approachY;
        }
        if (curX !== snapX) {
          this.ctx.lineTo(snapX, curY);
        }
      }
    } else {
      if (side === 'left' || side === 'right') {
        if (sx !== snapX) this.ctx.lineTo(snapX, sy);
      } else if (side === 'top' || side === 'bottom') {
        if (sy !== snapY) this.ctx.lineTo(sx, snapY);
      } else if (Math.abs(ex - sx) > Math.abs(ey - sy)) {
        if (sx !== snapX) this.ctx.lineTo(snapX, sy);
      } else if (sy !== snapY) {
        this.ctx.lineTo(sx, snapY);
      }
    }

    this.ctx.lineTo(snapX, snapY);
    this.ctx.lineTo(ex, ey);
    this.ctx.stroke();

    this.drawArrowHead(snapX, snapY, ex, ey);
  }
}
