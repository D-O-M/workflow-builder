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

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ReactiveFormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements AfterViewInit {
  protected readonly title = signal('workflow-builder');
  private readonly injector = inject(Injector);

  private readonly canvasRef =
    viewChild.required<ElementRef<HTMLCanvasElement>>('myCanvas');

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
    this.newNodePos.set({ x: canvasPos.x - off.x, y: canvasPos.y - off.y });
    this.nodeName.setValue('');
    this.showModal.set(true);
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
    const worldX = x - off.x;
    const worldY = y - off.y;
    if (this.dragConnection()) {
      this.dragConnection.update((d) =>
        d ? { ...d, x: worldX, y: worldY } : d,
      );
      const target = this.nodes().find(
        (n) => n.id !== this.dragConnection()!.from && this.inNode(worldX, worldY, n),
      );
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
    const worldX = x - off.x;
    const worldY = y - off.y;
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
    const worldX = x - off.x;
    const worldY = y - off.y;

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
          const connections = [...this.connections()];
          connections.push({ from: drag.from, to: target.id, anchor: drag.anchor });
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

  private inNode(x: number, y: number, node: Node): boolean {
    return (
      x >= node.x &&
      x <= node.x + node.width &&
      y >= node.y &&
      y <= node.y + node.height
    );
  }


  private anchorPositions(node: Node): Point[] {
    return [
      { x: node.x + node.width / 2, y: node.y },
      { x: node.x + node.width, y: node.y + node.height / 2 },
      { x: node.x + node.width / 2, y: node.y + node.height },
    ];
  }

  private inAnchorCircle(
    x: number,
    y: number,
    node: Node,
  ): number | null {
    const anchors = this.anchorPositions(node);
    for (let i = 0; i < anchors.length; i++) {
      if (Math.hypot(x - anchors[i].x, y - anchors[i].y) <= 10) {
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
  ): void {
    const snapX = this.snap(ex);
    const snapY = this.snap(ey);
    ctx.beginPath();
    ctx.moveTo(sx, sy);
    ctx.lineTo(snapX, sy);
    ctx.lineTo(snapX, snapY);
    ctx.lineTo(ex, ey);
    ctx.stroke();
    this.drawArrowHead(ctx, snapX, snapY, ex, ey);
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
    const off = this.offset();
    ctx.translate(off.x, off.y);

    for (const conn of this.connections()) {
      const from = this.nodes().find((n) => n.id === conn.from)!;
      const to = this.nodes().find((n) => n.id === conn.to)!;
      const start = this.anchorPositions(from)[conn.anchor];
      const end = { x: to.x, y: to.y + to.height / 2 };
      this.drawOrthogonalLine(ctx, start.x, start.y, end.x, end.y);
    }

    const hovered = this.hoveredNodeId();
    const drag = this.dragConnection();

    for (const node of this.nodes()) {
      const isHoveredDest =
        drag &&
        node.id !== drag.from &&
        hovered === node.id;
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
        ctx.arc(pos.x, pos.y, 10, 0, 2 * Math.PI);
        ctx.fill();
      } else if (!drag && hovered === node.id) {
        for (let i = 0; i < anchors.length; i++) {
          ctx.beginPath();
          ctx.globalAlpha =
            this.hoveredAnchor() === i ? 1 : 0.3;
          ctx.arc(anchors[i].x, anchors[i].y, 10, 0, 2 * Math.PI);
          ctx.fill();
          ctx.globalAlpha = 1;
        }
      }
    }

    if (drag) {
      const from = this.nodes().find((n) => n.id === drag.from)!;
      const start = this.anchorPositions(from)[drag.anchor];
      this.drawOrthogonalLine(ctx, start.x, start.y, drag.x, drag.y);
    }

    ctx.restore();
  }
}
