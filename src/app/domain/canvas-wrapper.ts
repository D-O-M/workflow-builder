import { NodeModel, Side } from './node-model';

export class CanvasWrapper {
  readonly arrowSize = 8;
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

    if (destNode) {
      const left = destNode.x - pad;
      const right = destNode.x + destNode.width + pad;
      const top = destNode.y - pad;
      const bottom = destNode.y + destNode.height + pad;

      if (side === 'left' || side === 'right') {
        const approachX = side === 'left' ? left : right;
        if (curY >= top && curY <= bottom) {
          const escapeY =
            curY < destNode.y + destNode.height / 2 ? top : bottom;
          lineTo(curX, escapeY);
        }
        if (curX !== approachX) {
          lineTo(approachX, curY);
        }
        if (curY !== snapY) {
          lineTo(curX, snapY);
        }
      } else if (side === 'top' || side === 'bottom') {
        const approachY = side === 'top' ? top : bottom;

        if (curX >= left && curX <= right) {
          const escapeX = curX < destNode.x + destNode.width / 2 ? left : right;
          lineTo(escapeX, curY);
        }
        if (curY !== approachY) {
          lineTo(curX, approachY);
        }
        if (curX !== snapX) {
          lineTo(snapX, curY);
        }
      }
    } else {
      if (side === 'left' || side === 'right') {
        if (curX !== snapX) lineTo(snapX, curY);
      } else if (side === 'top' || side === 'bottom') {
        if (curY !== snapY) lineTo(curX, snapY);
      } else if (Math.abs(ex - curX) > Math.abs(ey - curY)) {
        if (curX !== snapX) lineTo(snapX, curY);
      } else if (curY !== snapY) {
        lineTo(curX, snapY);
      }
    }

    lineTo(snapX, snapY);
    lineTo(ex, ey);
  }
}
