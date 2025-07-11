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
    startSide?: Side,
  ): void {
    const snapX = this.snap(ex);
    const snapY = this.snap(ey);
    this.ctx.beginPath();
    this.ctx.moveTo(sx, sy);

    const pad = this.nodeMargin;

    let curX = sx;
    let curY = sy;

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
      this.ctx.lineTo(curX, curY);
    }

    if (destNode) {
      const left = destNode.x - pad;
      const right = destNode.x + destNode.width + pad;
      const top = destNode.y - pad;
      const bottom = destNode.y + destNode.height + pad;

      if (side === 'left' || side === 'right') {
        const approachX = side === 'left' ? left : right;
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
        if (curX !== snapX) this.ctx.lineTo(snapX, curY);
      } else if (side === 'top' || side === 'bottom') {
        if (curY !== snapY) this.ctx.lineTo(curX, snapY);
      } else if (Math.abs(ex - curX) > Math.abs(ey - curY)) {
        if (curX !== snapX) this.ctx.lineTo(snapX, curY);
      } else if (curY !== snapY) {
        this.ctx.lineTo(curX, snapY);
      }
    }

    this.ctx.lineTo(snapX, snapY);
    this.ctx.lineTo(ex, ey);
    this.ctx.stroke();

    this.drawArrowHead(snapX, snapY, ex, ey);
  }
}
