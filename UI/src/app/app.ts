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
import { HttpClient } from '@angular/common/http';
import { CanvasWrapper } from './domain/canvas-wrapper';
import { ConnectionModel } from './domain/connection-model';
import { NodeModel, Point, Side } from './domain/node-model';
import { Task } from './domain/task';

interface DragNode {
  id: number;
  dx: number;
  dy: number;
}

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

  private readonly selectRef =
    viewChild<ElementRef<HTMLSelectElement>>('taskSelect');

  protected readonly zoom = signal(1);

  private get canvas(): HTMLCanvasElement {
    return this.canvasRef().nativeElement;
  }

  private canvasWrapper?: CanvasWrapper;

  protected readonly nodes = signal<NodeModel[]>([]);
  protected readonly connections = signal<ConnectionModel[]>([]);

  private readonly gridSize = 20;
  private readonly nodeMargin = 20;

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
    side?: Side;
  } | null>(null);

  protected readonly showModal = signal(false);
  private readonly newNodePos = signal<{ x: number; y: number } | null>(null);

  protected readonly tasks = signal<Task[]>([]);
  protected readonly selectedTask = new FormControl('', { nonNullable: true });
  private readonly http = inject(HttpClient);

  ngAfterViewInit(): void {
    const c = this.canvasRef();
    if (!c) return;
    const ctx = c.nativeElement.getContext('2d')!;
    this.canvasWrapper = new CanvasWrapper(ctx, this.gridSize, this.nodeMargin);
    c.nativeElement.width = c.nativeElement.clientWidth;
    c.nativeElement.height = c.nativeElement.clientHeight;

    this.http.get<Task[]>('/api/tasks').subscribe((t) => {
      this.tasks.set(t);
      if (t.length > 0) {
        this.selectedTask.setValue(t[0].name);
      }
    });

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
    const tasks = this.tasks();
    this.selectedTask.setValue(tasks[0]?.name ?? '');
    this.showModal.set(true);
    queueMicrotask(() => this.selectRef()?.nativeElement.focus());
  }

  protected createNode(): void {
    const name = this.selectedTask.value;
    const pos = this.newNodePos();
    if (!name || !pos) {
      this.showModal.set(false);
      return;
    }
    const nodes = [...this.nodes()];
    const wrapper = this.canvasWrapper!;
    nodes.push(
      new NodeModel(
        Date.now(),
        name,
        wrapper.snap(pos.x),
        wrapper.snap(pos.y),
        240,
        160,
      ),
    );
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
      const conns = this.connections();
      const target = this.nodes().find((n) => {
        if (n.id === drag.from || !this.inNodeBuffer(worldX, worldY, n))
          return false;
        return !conns.some(
          (c) =>
            (c.from.id === drag.from && c.to.id === n.id) ||
            (c.to.id === drag.from && c.from.id === n.id),
        );
      });
      if (target) {
        const dir = this.sideFromPoint(worldX, worldY, target);
        const snap = this.snapPointFixedSide(worldX, worldY, target, dir);
        this.dragConnection.update((d) =>
          d ? { ...d, x: snap.x, y: snap.y, side: dir } : d,
        );
        this.hoveredNodeId.set(target.id);
      } else {
        this.dragConnection.update((d) =>
          d ? { ...d, x: worldX, y: worldY, side: undefined } : d,
        );
        this.hoveredNodeId.set(null);
      }
      return;
    }
    if (this.dragNode()) {
      const drag = this.dragNode()!;
      const wrapper = this.canvasWrapper!;
      this.nodes.update((nodes) =>
        nodes.map((n) => {
          if (n.id === drag.id) {
            n.x = wrapper.snap(worldX - drag.dx);
            n.y = wrapper.snap(worldY - drag.dy);
          }
          return n;
        }),
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
    const node = this.nodes().find((n) => this.inNodeBuffer(worldX, worldY, n));
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
          side: undefined,
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
      const conns = this.connections();
      const target = this.nodes().find((n) => {
        if (n.id === drag.from || !this.inNodeBuffer(worldX, worldY, n))
          return false;
        return !conns.some(
          (c) =>
            (c.from.id === drag.from && c.to.id === n.id) ||
            (c.to.id === drag.from && c.from.id === n.id),
        );
      });
      if (target) {
        const exists = this.connections().some(
          (c) =>
            (c.from.id === drag.from && c.to.id === target.id) ||
            (c.from.id === target.id && c.to.id === drag.from),
        );
        if (!exists) {
          const dir = this.sideFromPoint(worldX, worldY, target);
          const snap = this.snapPointFixedSide(worldX, worldY, target, dir);
          const connections = [...this.connections()];
          const fromNode = this.nodes().find((n) => n.id === drag.from)!;
          connections.push(
            new ConnectionModel(fromNode, target, drag.anchor, dir, snap.pos),
          );
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

  private inNode(x: number, y: number, node: NodeModel): boolean {
    return node.contains(x, y);
  }

  private inNodeBuffer(x: number, y: number, node: NodeModel): boolean {
    return node.containsBuffered(x, y, this.nodeMargin);
  }

  private clamp(v: number, min: number, max: number): number {
    return NodeModel.clamp(v, min, max);
  }

  private snapPointToSide(
    x: number,
    y: number,
    node: NodeModel,
  ): { x: number; y: number; side: Side; pos: number } {
    return node.snapPointToSide(x, y);
  }

  private anchorPositions(node: NodeModel): Point[] {
    return node.anchorPositions();
  }

  private anchorSide(idx: number): Side {
    return NodeModel.anchorSide(idx);
  }

  private oppositeAnchor(idx: number): number {
    return NodeModel.oppositeAnchor(idx);
  }

  private snapPointFixedSide(
    x: number,
    y: number,
    node: NodeModel,
    side: Side,
  ): { x: number; y: number; side: Side; pos: number } {
    return node.snapPointFixedSide(x, y, side);
  }

  private sideFromPoint(x: number, y: number, node: NodeModel): Side {
    return node.sideFromPoint(x, y);
  }

  private inAnchorCircle(x: number, y: number, node: NodeModel): number | null {
    const anchors = this.anchorPositions(node);
    for (let i = 0; i < anchors.length; i++) {
      if (
        Math.hypot(x - anchors[i].x, y - anchors[i].y) <= node.anchorRadius(i)
      ) {
        return i;
      }
    }
    return null;
  }

  private snap(value: number): number {
    return this.canvasWrapper!.snap(value);
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
    this.canvasWrapper!.drawWrappedText(text, x, y, maxWidth);
  }

  private drawOrthogonalLine(
    ctx: CanvasRenderingContext2D,
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    obstacles: NodeModel[],
    side?: Side,
    destNode?: NodeModel,
    startSide?: Side,
  ): void {
    this.canvasWrapper!.drawOrthogonalLine(
      sx,
      sy,
      ex,
      ey,
      obstacles,
      side,
      destNode,
      startSide,
    );
  }

  private draw(): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvas;
    if (!wrapper || !canvas) return;
    const ctx = wrapper.ctx;

    wrapper.clear(canvas.width, canvas.height);

    ctx.save();
    const zoom = this.zoom();
    ctx.scale(zoom, zoom);
    const off = this.offset();
    ctx.translate(off.x, off.y);

    for (const conn of this.connections()) {
      const obstacles = this.nodes().filter(
        (n) => n.id !== conn.from.id && n.id !== conn.to.id,
      );
      conn.draw(wrapper, obstacles);
    }

    const hovered = this.hoveredNodeId();
    const drag = this.dragConnection();

    for (const node of this.nodes()) {
      const isHoveredDest =
        drag && node.id !== drag.from && hovered === node.id;
      const eligible =
        drag &&
        node.id !== drag.from &&
        !this.connections().some(
          (c) =>
            (c.from.id === drag.from && c.to.id === node.id) ||
            (c.to.id === drag.from && c.from.id === node.id),
        );

      let saved = false;
      if (isHoveredDest) {
        ctx.save();
        ctx.strokeStyle = 'rgba(0,0,255,0.5)';
        ctx.setLineDash([4, 4]);
        saved = true;
      } else if (eligible) {
        ctx.save();
        ctx.globalAlpha = 0.3;
        saved = true;
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
      } else if (saved) {
        ctx.restore();
      }

      const anchors = this.anchorPositions(node);
      if (drag && drag.from === node.id) {
        const pos = anchors[drag.anchor];
        ctx.beginPath();
        ctx.arc(pos.x, pos.y, node.anchorRadius(drag.anchor), 0, 2 * Math.PI);
        ctx.fill();
      } else if (!drag && hovered === node.id) {
        for (let i = 0; i < anchors.length; i++) {
          ctx.beginPath();
          ctx.globalAlpha = this.hoveredAnchor() === i ? 1 : 0.3;
          ctx.arc(
            anchors[i].x,
            anchors[i].y,
            node.anchorRadius(i),
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
        if (hoveredNode && drag.side) {
          const pos = this.snapPointFixedSide(
            drag.x,
            drag.y,
            hoveredNode,
            drag.side,
          ).pos;
          const obstacles = this.nodes().filter(
            (n) => n.id !== from.id && n.id !== hoveredNode.id,
          );
          new ConnectionModel(
            from,
            hoveredNode,
            drag.anchor,
            drag.side,
            pos,
          ).draw(wrapper, obstacles);
        } else {
          const obstacles = this.nodes().filter(
            (n) => n.id !== from.id && n.id !== hoveredNode?.id,
          );
          this.drawOrthogonalLine(
            ctx,
            start.x,
            start.y,
            drag.x,
            drag.y,
            obstacles,
            drag.side ?? this.anchorSide(drag.anchor),
            hoveredNode,
            this.anchorSide(drag.anchor),
          );
        }
      }
    }

    ctx.restore();
  }
}
