import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  effect,
  ElementRef,
  inject,
  Injector,
  runInInjectionContext,
  signal,
  viewChild,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

interface Node {
  id: number;
  name: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

interface Connection {
  from: number;
  to: number;
  anchor: number;
  side: 'left' | 'right' | 'top' | 'bottom';
  pos: number;
}

interface DragNode {
  id: number;
  dx: number;
  dy: number;
}

interface Point {
  x: number;
  y: number;
}

type Side = 'left' | 'right' | 'top' | 'bottom';

@Component({
  selector: 'app-root',
  imports: [ReactiveFormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements AfterViewInit {
  protected readonly title = signal('workflow-builder');
  protected readonly Math = Math;
  private readonly injector = inject(Injector);

  private readonly canvasRef =
    viewChild.required<ElementRef<HTMLCanvasElement>>('myCanvas');

  private readonly inputRef =
    viewChild<ElementRef<HTMLInputElement>>('nameInput');

  protected readonly zoom = signal(1);

  private get canvas(): HTMLCanvasElement {
    return this.canvasRef().nativeElement;
  }

  private ctx?: CanvasRenderingContext2D;

  protected readonly nodes = signal<Node[]>([]);
  protected readonly connections = signal<Connection[]>([]);

  private readonly gridSize = 20;

  protected readonly offset = signal<Point>({ x: 0, y: 0 });
  protected readonly dragOffset = signal<Point | null>(null);
  protected readonly dragNode = signal<DragNode | null>(null);
  private animationId: number | null = null;

  protected readonly hoveredNodeId = signal<number | null>(null);
  protected readonly hoveredAnchor = signal<number | null>(null);

  protected readonly dragConnection = signal<{
    from: number;
    anchor: number;
    x: number;
    y: number;
  } | null>(null);

  protected readonly showModal = signal(false);
  private readonly newNodePos = signal<{ x: number; y: number } | null>(null);

  protected readonly nodeName = new FormControl('', { nonNullable: true });

  ngAfterViewInit(): void {
    const c = this.canvasRef();
    if (!c) return;
    this.ctx = c.nativeElement.getContext('2d')!;
    c.nativeElement.width = c.nativeElement.clientWidth;
    c.nativeElement.height = c.nativeElement.clientHeight;

    runInInjectionContext(this.injector, () => {
      effect(() => {
        this.draw();
      });
    });
  }

  protected openModal(event: MouseEvent): void {
    const rect = this.canvas!.getBoundingClientRect();
    const canvasPos = {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
    };
    const off = this.offset();
    const zoom = this.zoom();
    this.newNodePos.set({
      x: (canvasPos.x - off.x) / zoom,
      y: (canvasPos.y - off.y) / zoom,
    });
    this.nodeName.setValue('');
    this.showModal.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  protected createNode(): void {
    const name = this.nodeName.value.trim();
    const pos = this.newNodePos();
    if (!name || !pos) {
      this.showModal.set(false);
      return;
    }
    const nodes = [...this.nodes()];
    nodes.push({
      id: Date.now(),
      name,
      x: this.snap(pos.x),
      y: this.snap(pos.y),
      width: 240,
      height: 160,
    });
    this.nodes.set(nodes);
    this.showModal.set(false);
  }

  private getMousePos(event: MouseEvent): { x: number; y: number } {
    const rect = this.canvas!.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  protected onMouseMove(event: MouseEvent): void {
    const { x, y } = this.getMousePos(event);
    const off = this.offset();
    const zoom = this.zoom();
    const worldX = (x - off.x) / zoom;
    const worldY = (y - off.y) / zoom;
    if (this.dragConnection()) {
      const drag = this.dragConnection()!;
      const from = this.nodes().find((n) => n.id === drag.from)!;
      const side = this.anchorSide(drag.anchor);
      // switch anchor if dragged beyond opposite side
      if (
        (side === 'left' && worldX > from.x + from.width) ||
        (side === 'right' && worldX < from.x) ||
        (side === 'top' && worldY > from.y + from.height) ||
        (side === 'bottom' && worldY < from.y)
      ) {
        const newAnchor = this.oppositeAnchor(drag.anchor);
        this.dragConnection.update((d) =>
          d ? { ...d, anchor: newAnchor } : d,
        );
      }
      const curAnchor = this.dragConnection()!.anchor;
      const target = this.nodes().find(
        (n) => n.id !== drag.from && this.inNode(worldX, worldY, n),
      );
      if (target) {
        const dir = this.directionFromTo(from, target);
        const snap = this.snapPointFixedSide(worldX, worldY, target, dir);
        this.dragConnection.update((d) =>
          d ? { ...d, x: snap.x, y: snap.y } : d,
        );
      } else {
        this.dragConnection.update((d) =>
          d ? { ...d, x: worldX, y: worldY } : d,
        );
      }
      this.hoveredNodeId.set(target ? target.id : null);
      return;
    }
    if (this.dragNode()) {
      const drag = this.dragNode()!;
      this.nodes.update((nodes) =>
        nodes.map((n) =>
          n.id === drag.id
            ? {
                ...n,
                x: this.snap(worldX - drag.dx),
                y: this.snap(worldY - drag.dy),
              }
            : n,
        ),
      );
      return;
    }
    if (this.dragOffset()) {
      const last = this.dragOffset()!;
      this.offset.update((o) => ({
        x: o.x + (x - last.x),
        y: o.y + (y - last.y),
      }));
      this.dragOffset.set({ x, y });
      return;
    }
    const node = this.nodes().find((n) => this.inNode(worldX, worldY, n));
    this.hoveredNodeId.set(node ? node.id : null);
    if (node) {
      const idx = this.inAnchorCircle(worldX, worldY, node);
      this.hoveredAnchor.set(idx);
    } else {
      this.hoveredAnchor.set(null);
    }
  }

  protected onMouseDown(event: MouseEvent): void {
    const { x, y } = this.getMousePos(event);
    const off = this.offset();
    const zoom = this.zoom();
    const worldX = (x - off.x) / zoom;
    const worldY = (y - off.y) / zoom;
    const hovered = this.nodes().find((n) => this.inNode(worldX, worldY, n));
    if (hovered) {
      const anchor = this.inAnchorCircle(worldX, worldY, hovered);
      if (anchor !== null) {
        this.dragConnection.set({
          from: hovered.id,
          anchor,
          x: worldX,
          y: worldY,
        });
        this.startAnimation();
      } else {
        this.dragNode.set({
          id: hovered.id,
          dx: worldX - hovered.x,
          dy: worldY - hovered.y,
        });
        this.startAnimation();
      }
      this.hoveredNodeId.set(hovered.id);
      this.hoveredAnchor.set(anchor);
    } else {
      this.dragOffset.set({ x, y });
      this.startAnimation();
    }
  }

  protected onMouseUp(event: MouseEvent): void {
    const { x, y } = this.getMousePos(event);
    const off = this.offset();
    const zoom = this.zoom();
    const worldX = (x - off.x) / zoom;
    const worldY = (y - off.y) / zoom;

    const drag = this.dragConnection();
    if (drag) {
      const target = this.nodes().find(
        (n) => n.id !== drag.from && this.inNode(worldX, worldY, n),
      );
      if (target) {
        const exists = this.connections().some(
          (c) =>
            (c.from === drag.from && c.to === target.id) ||
            (c.from === target.id && c.to === drag.from),
        );
        if (!exists) {
          const from = this.nodes().find((n) => n.id === drag.from)!;
          const dir = this.directionFromTo(from, target);
          const snap = this.snapPointFixedSide(worldX, worldY, target, dir);
          const connections = [...this.connections()];
          connections.push({
            from: drag.from,
            to: target.id,
            anchor: drag.anchor,
            side: dir,
            pos: snap.pos,
          });
          this.connections.set(connections);
        }
      }
      this.dragConnection.set(null);
      this.hoveredAnchor.set(null);
      this.hoveredNodeId.set(null);
    }

    if (this.dragNode()) {
      this.dragNode.set(null);
    }

    if (this.dragOffset()) {
      this.dragOffset.set(null);
    }

    this.stopAnimation();
  }

  protected onMouseLeave(): void {
    this.dragConnection.set(null);
    this.dragNode.set(null);
    this.dragOffset.set(null);
    this.hoveredNodeId.set(null);
    this.hoveredAnchor.set(null);
    this.stopAnimation();
  }

  protected onWheel(event: WheelEvent): void {
    event.preventDefault();
    const delta = Math.sign(event.deltaY);
    this.zoom.update((z) => {
      let nz = z - delta * 0.05;
      nz = Math.min(3, Math.max(0.05, nz));
      return parseFloat(nz.toFixed(2));
    });
  }

  protected resetView(): void {
    this.zoom.set(1);
    this.offset.set({ x: 0, y: 0 });
  }

  private inNode(x: number, y: number, node: Node): boolean {
    return (
      x >= node.x &&
      x <= node.x + node.width &&
      y >= node.y &&
      y <= node.y + node.height
    );
  }

  private clamp(v: number, min: number, max: number): number {
    return Math.min(Math.max(v, min), max);
  }

  private snapPointToSide(
    x: number,
    y: number,
    node: Node,
  ): {
    x: number;
    y: number;
    side: 'left' | 'right' | 'top' | 'bottom';
    pos: number;
  } {
    const left = Math.abs(x - node.x);
    const right = Math.abs(x - (node.x + node.width));
    const top = Math.abs(y - node.y);
    const bottom = Math.abs(y - (node.y + node.height));
    const min = Math.min(left, right, top, bottom);
    if (min === left) {
      const yy = this.clamp(y, node.y, node.y + node.height);
      return { x: node.x, y: yy, side: 'left', pos: yy - node.y };
    } else if (min === right) {
      const yy = this.clamp(y, node.y, node.y + node.height);
      return { x: node.x + node.width, y: yy, side: 'right', pos: yy - node.y };
    } else if (min === top) {
      const xx = this.clamp(x, node.x, node.x + node.width);
      return { x: xx, y: node.y, side: 'top', pos: xx - node.x };
    } else {
      const xx = this.clamp(x, node.x, node.x + node.width);
      return {
        x: xx,
        y: node.y + node.height,
        side: 'bottom',
        pos: xx - node.x,
      };
    }
  }

  private anchorPositions(node: Node): Point[] {
    const anchors: Point[] = [];
    const offs = [0.25, 0.5, 0.75];
    for (const o of offs) {
      anchors.push({ x: node.x + node.width * o, y: node.y });
    }
    for (const o of offs) {
      anchors.push({ x: node.x + node.width, y: node.y + node.height * o });
    }
    for (const o of offs) {
      anchors.push({ x: node.x + node.width * o, y: node.y + node.height });
    }
    for (const o of offs) {
      anchors.push({ x: node.x, y: node.y + node.height * o });
    }
    return anchors;
  }

  private anchorSide(idx: number): Side {
    if (idx < 3) return 'top';
    if (idx < 6) return 'right';
    if (idx < 9) return 'bottom';
    return 'left';
  }

  private oppositeAnchor(idx: number): number {
    return [6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5][idx];
  }

  private snapPointFixedSide(
    x: number,
    y: number,
    node: Node,
    side: Side,
  ): { x: number; y: number; side: Side; pos: number } {
    switch (side) {
      case 'left':
        const yyL = this.clamp(y, node.y, node.y + node.height);
        return { x: node.x, y: yyL, side: 'left', pos: yyL - node.y };
      case 'right':
        const yyR = this.clamp(y, node.y, node.y + node.height);
        return {
          x: node.x + node.width,
          y: yyR,
          side: 'right',
          pos: yyR - node.y,
        };
      case 'top':
        const xxT = this.clamp(x, node.x, node.x + node.width);
        return { x: xxT, y: node.y, side: 'top', pos: xxT - node.x };
      default:
        const xxB = this.clamp(x, node.x, node.x + node.width);
        return {
          x: xxB,
          y: node.y + node.height,
          side: 'bottom',
          pos: xxB - node.x,
        };
    }
  }

  private directionFromTo(from: Node, to: Node): Side {
    const ax = from.x + from.width / 2;
    const ay = from.y + from.height / 2;
    const bx = to.x + to.width / 2;
    const by = to.y + to.height / 2;
    if (Math.abs(ax - bx) > Math.abs(ay - by)) {
      return ax < bx ? 'left' : 'right';
    }
    return ay < by ? 'top' : 'bottom';
  }

  private anchorRadius(idx: number): number {
    return [1, 4, 7, 10].includes(idx) ? 12 : 10;
  }

  private inAnchorCircle(x: number, y: number, node: Node): number | null {
    const anchors = this.anchorPositions(node);
    for (let i = 0; i < anchors.length; i++) {
      if (
        Math.hypot(x - anchors[i].x, y - anchors[i].y) <= this.anchorRadius(i)
      ) {
        return i;
      }
    }
    return null;
  }

  private snap(value: number): number {
    return Math.round(value / this.gridSize) * this.gridSize;
  }

  private startAnimation(): void {
    if (this.animationId !== null) return;
    const step = () => {
      this.draw();
      this.animationId = requestAnimationFrame(step);
    };
    this.animationId = requestAnimationFrame(step);
  }

  private stopAnimation(): void {
    if (this.animationId !== null) {
      cancelAnimationFrame(this.animationId);
      this.animationId = null;
    }
  }

  private drawWrappedText(
    ctx: CanvasRenderingContext2D,
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
      if (ctx.measureText(test).width > maxWidth && line) {
        ctx.fillText(line, x, y);
        line = word;
        y += lineHeight;
      } else {
        line = test;
      }
    }
    ctx.fillText(line, x, y);
  }

  private drawOrthogonalLine(
    ctx: CanvasRenderingContext2D,
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    side?: Side,
    arrowTarget?: Point,
  ): void {
    const snapX = this.snap(ex);
    const snapY = this.snap(ey);
    ctx.beginPath();
    ctx.moveTo(sx, sy);
    const offset = 20;
    if (side === 'left') {
      if (ex >= sx) {
        ctx.lineTo(sx - offset, sy);
        ctx.lineTo(sx - offset, snapY);
      } else {
        ctx.lineTo(snapX, sy);
      }
    } else if (side === 'right') {
      if (ex <= sx) {
        ctx.lineTo(sx + offset, sy);
        ctx.lineTo(sx + offset, snapY);
      } else {
        ctx.lineTo(snapX, sy);
      }
    } else if (side === 'top') {
      if (ey >= sy) {
        ctx.lineTo(sx, sy - offset);
        ctx.lineTo(snapX, sy - offset);
      } else {
        ctx.lineTo(sx, snapY);
      }
    } else if (side === 'bottom') {
      if (ey <= sy) {
        ctx.lineTo(sx, sy + offset);
        ctx.lineTo(snapX, sy + offset);
      } else {
        ctx.lineTo(sx, snapY);
      }
    } else {
      ctx.lineTo(snapX, sy);
    }
    ctx.lineTo(snapX, snapY);

    if (arrowTarget) {
      const dx = arrowTarget.x - ex;
      const dy = arrowTarget.y - ey;
      let destSide: Side;
      if (Math.abs(dx) > Math.abs(dy)) {
        destSide = dx > 0 ? 'right' : 'left';
      } else {
        destSide = dy > 0 ? 'bottom' : 'top';
      }
      if (destSide === 'left') {
        ctx.lineTo(ex + offset, snapY);
        ctx.lineTo(ex + offset, ey);
      } else if (destSide === 'right') {
        ctx.lineTo(ex - offset, snapY);
        ctx.lineTo(ex - offset, ey);
      } else if (destSide === 'top') {
        ctx.lineTo(snapX, ey + offset);
        ctx.lineTo(ex, ey + offset);
      } else {
        ctx.lineTo(snapX, ey - offset);
        ctx.lineTo(ex, ey - offset);
      }
    }

    ctx.lineTo(ex, ey);
    ctx.stroke();

    const arrowSize = 8;
    let bx = ex;
    let by = ey;
    if (arrowTarget) {
      const dx = arrowTarget.x - ex;
      const dy = arrowTarget.y - ey;
      if (Math.abs(dx) > Math.abs(dy)) {
        bx = dx > 0 ? ex - arrowSize : ex + arrowSize;
      } else {
        by = dy > 0 ? ey - arrowSize : ey + arrowSize;
      }
    }

    this.drawArrowHead(ctx, bx, by, ex, ey);
  }

  private drawArrowHead(
    ctx: CanvasRenderingContext2D,
    fromX: number,
    fromY: number,
    toX: number,
    toY: number,
  ): void {
    const angle = Math.atan2(toY - fromY, toX - fromX);
    const size = 8;
    ctx.beginPath();
    ctx.moveTo(toX, toY);
    ctx.lineTo(
      toX - size * Math.cos(angle - Math.PI / 6),
      toY - size * Math.sin(angle - Math.PI / 6),
    );
    ctx.lineTo(
      toX - size * Math.cos(angle + Math.PI / 6),
      toY - size * Math.sin(angle + Math.PI / 6),
    );
    ctx.closePath();
    ctx.fill();
  }

  private draw(): void {
    const ctx = this.ctx;
    const canvas = this.canvas;
    if (!ctx || !canvas) return;

    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.strokeStyle = 'blue';
    ctx.fillStyle = 'blue';
    ctx.lineWidth = 2;

    ctx.save();
    const zoom = this.zoom();
    ctx.scale(zoom, zoom);
    const off = this.offset();
    ctx.translate(off.x, off.y);

    for (const conn of this.connections()) {
      const from = this.nodes().find((n) => n.id === conn.from)!;
      const to = this.nodes().find((n) => n.id === conn.to)!;
      const start = this.anchorPositions(from)[conn.anchor];
      let end: Point;
      switch (conn.side) {
        case 'left':
          end = { x: to.x, y: to.y + conn.pos };
          break;
        case 'right':
          end = { x: to.x + to.width, y: to.y + conn.pos };
          break;
        case 'top':
          end = { x: to.x + conn.pos, y: to.y };
          break;
        default:
          end = { x: to.x + conn.pos, y: to.y + to.height };
      }
      this.drawOrthogonalLine(
        ctx,
        start.x,
        start.y,
        end.x,
        end.y,
        this.anchorSide(conn.anchor),
        { x: to.x + to.width / 2, y: to.y + to.height / 2 },
      );
    }

    const hovered = this.hoveredNodeId();
    const drag = this.dragConnection();

    for (const node of this.nodes()) {
      const isHoveredDest =
        drag && node.id !== drag.from && hovered === node.id;
      if (isHoveredDest) {
        ctx.save();
        ctx.strokeStyle = 'rgba(0,0,255,0.5)';
        ctx.setLineDash([4, 4]);
      }

      ctx.strokeRect(node.x, node.y, node.width, node.height);
      this.drawWrappedText(
        ctx,
        node.name,
        node.x + 10,
        node.y + 20,
        node.width - 20,
      );

      if (isHoveredDest) {
        ctx.restore();
      }

      const anchors = this.anchorPositions(node);
      if (drag && drag.from === node.id) {
        const pos = anchors[drag.anchor];
        ctx.beginPath();
        ctx.arc(pos.x, pos.y, this.anchorRadius(drag.anchor), 0, 2 * Math.PI);
        ctx.fill();
      } else if (!drag && hovered === node.id) {
        for (let i = 0; i < anchors.length; i++) {
          ctx.beginPath();
          ctx.globalAlpha = this.hoveredAnchor() === i ? 1 : 0.3;
          ctx.arc(
            anchors[i].x,
            anchors[i].y,
            this.anchorRadius(i),
            0,
            2 * Math.PI,
          );
          ctx.fill();
          ctx.globalAlpha = 1;
        }
      }
    }

    if (drag) {
      const from = this.nodes().find((n) => n.id === drag.from)!;
      if (!this.inNode(drag.x, drag.y, from)) {
        const start = this.anchorPositions(from)[drag.anchor];
        const hoveredNode =
          hovered && hovered !== drag.from
            ? this.nodes().find((n) => n.id === hovered)
            : undefined;
        this.drawOrthogonalLine(
          ctx,
          start.x,
          start.y,
          drag.x,
          drag.y,
          this.anchorSide(drag.anchor),
          hoveredNode
            ? {
                x: hoveredNode.x + hoveredNode.width / 2,
                y: hoveredNode.y + hoveredNode.height / 2,
              }
            : undefined,
        );
      }
    }

    ctx.restore();
  }
}
