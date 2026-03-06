export interface Point {
    x: number;
    y: number;
}

export type Side = 'left' | 'right' | 'top' | 'bottom';

export class NodeModel {
    static buffer = 20;

    static anchorSide(idx: number): Side {
        if (idx < 3) return 'top';
        if (idx < 6) return 'right';
        if (idx < 9) return 'bottom';
        return 'left';
    }

    static oppositeAnchor(idx: number): number {
        return [6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5][idx];
    }

    static clamp(v: number, min: number, max: number): number {
        return Math.min(Math.max(v, min), max);
    }

    contains(x: number, y: number): boolean {
        return (
            x >= this.x &&
            x <= this.x + this.width &&
            y >= this.y &&
            y <= this.y + this.height
        );
    }

    containsBuffered(x: number, y: number, buffer: number): boolean {
        return (
            x >= this.x - buffer &&
            x <= this.x + this.width + buffer &&
            y >= this.y - buffer &&
            y <= this.y + this.height + buffer
        );
    }

    constructor(
        public id: number,
        public taskVersionId: number,
        public name: string,
        public x: number,
        public y: number,
        public width: number,
        public height: number,
    ) {}

    draw(ctx: CanvasRenderingContext2D, drawText: (
      ctx: CanvasRenderingContext2D,
      text: string,
      x: number,
      y: number,
      maxWidth: number) => void): void {
        ctx.strokeRect(this.x, this.y, this.width, this.height);
        drawText(ctx, this.name, this.x + 10, this.y + 20, this.width - 20);
    }

    anchorPositions(): Point[] {
        const anchors: Point[] = [];
        const offs = [0.25, 0.5, 0.75];
        for (const o of offs) {
            anchors.push({ x: this.x + this.width * o, y: this.y });
        }
        for (const o of offs) {
            anchors.push({ x: this.x + this.width, y: this.y + this.height * o });
        }
        for (const o of offs) {
            anchors.push({ x: this.x + this.width * o, y: this.y + this.height });
        }
        for (const o of offs) {
            anchors.push({ x: this.x, y: this.y + this.height * o });
        }
        return anchors;
    }

    anchorRadius(idx: number): number {
        return [1, 4, 7, 10].includes(idx) ? 12 : 10;
    }

    inAnchorCircle(x: number, y: number): number | null {
        const anchors = this.anchorPositions();
        for (let i = 0; i < anchors.length; i++) {
            if (Math.hypot(x - anchors[i].x, y - anchors[i].y) <= this.anchorRadius(i)) {
                return i;
            }
        }
        return null;
    }

    sideFromPoint(x: number, y: number): Side {
        const cx = this.x + this.width / 2;
        const cy = this.y + this.height / 2;
        const dx = x - cx;
        const dy = y - cy;
        if (Math.abs(dx) * this.height > Math.abs(dy) * this.width) {
            return dx < 0 ? 'left' : 'right';
        }
        return dy < 0 ? "top" : "bottom";
    }

    snapPointToSide(x: number, y: number): { x: number; y: number; side: Side; pos: number } {
        const left = Math.abs(x - this.x);
        const right = Math.abs(x - (this.x + this.width));
        const top = Math.abs(y - this.y);
        const bottom = Math.abs(y - (this.y + this.height));
        const min = Math.min(left, right, top, bottom);
        if (min === left) {
            const yy = NodeModel.clamp(y, this.y, this.y + this.height);
            return { x: this.x, y: yy, side: "left", pos: yy - this.y };
        } else if (min === right) {
            const yy = NodeModel.clamp(y, this.y, this.y + this.height);
            return { x: this.x + this.width, y: yy, side: "right", pos: yy - this.y };
        } else if (min === top) {
            const xx = NodeModel.clamp(x, this.x, this.x + this.width);
            return { x: xx, y: this.y, side: "top", pos: xx - this.x };
        } else {
            const xx = NodeModel.clamp(x, this.x, this.x + this.width);
            return { x: xx, y: this.y + this.height, side: "bottom", pos: xx - this.x };
        }
    }

    snapPointFixedSide(x: number, y: number, side: Side): { x: number; y: number; side: Side; pos: number } {
        switch (side) {
            case "left":
                const yyL = NodeModel.clamp(y, this.y, this.y + this.height);
                return { x: this.x, y: yyL, side: "left", pos: yyL - this.y };
            case "right":
                const yyR = NodeModel.clamp(y, this.y, this.y + this.height);
                return { x: this.x + this.width, y: yyR, side: "right", pos: yyR - this.y };
            case "top":
                const xxT = NodeModel.clamp(x, this.x, this.x + this.width);
                return { x: xxT, y: this.y, side: "top", pos: xxT - this.x };
            default:
                const xxB = NodeModel.clamp(x, this.x, this.x + this.width);
                return { x: xxB, y: this.y + this.height, side: "bottom", pos: xxB - this.x };
        }
    }
}
