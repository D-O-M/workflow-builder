import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { ConnectionModel } from '../domain/connection-model';
import { CanvasWrapper } from '../domain/canvas-wrapper';
import { NodeModel, Point, Side } from '../domain/node-model';

interface DragNode {
  id: number;
  dx: number;
  dy: number;
}

@Component({
  selector: 'app-workflow-canvas',
  templateUrl: './workflow-canvas.component.html',
  styleUrl: './workflow-canvas.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowCanvasComponent implements AfterViewInit, OnDestroy {
  readonly nodes = input.required<NodeModel[]>();
  readonly connections = input.required<ConnectionModel[]>();
  readonly mode = input<'edit' | 'readonly'>('edit');

  readonly nodeMoved = output<{ nodeId: number; coordinates: Point }>();
  readonly connectionCreated = output<{
    sourceNodeId: number;
    destinationNodeId: number;
    anchor: number;
    side: Side;
    position: number;
  }>();

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly gridSize = 20;
  private canvasWrapper?: CanvasWrapper;
  private animationId: number | null = null;
  private zoomRedrawTimeoutId: number | null = null;
  private resizeObserver?: ResizeObserver;

  protected readonly hoveredNodeId = signal<number | null>(null);
  protected readonly hoveredAnchor = signal<number | null>(null);
  protected readonly dragOffset = signal<Point | null>(null);
  protected readonly dragNode = signal<DragNode | null>(null);
  protected readonly dragConnection = signal<{
    from: number;
    anchor: number;
    x: number;
    y: number;
    side?: Side;
  } | null>(null);
  readonly zoomPercent = signal(100);

  ngAfterViewInit(): void {
    const canvas = this.canvasRef().nativeElement;
    const context = canvas.getContext('2d');
    if (!context) {
      return;
    }

    this.canvasWrapper = new CanvasWrapper(context, this.gridSize);
    this.syncCanvasBackingSize();

    this.resizeObserver = new ResizeObserver(() => {
      this.syncCanvasBackingSize();
      this.draw();
    });
    this.resizeObserver.observe(canvas);

    effect(() => {
      const wrapper = this.canvasWrapper;
      if (!wrapper) {
        return;
      }

      this.nodes();
      this.connections();
      this.hoveredNodeId();
      this.hoveredAnchor();
      this.dragConnection();
      this.dragNode();
      this.dragOffset();
      wrapper.fontReady();
      this.zoomPercent.set(Math.round(wrapper.zoomLevel() * 100));
      wrapper.viewportOffset();
      this.draw();
    });
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    if (this.zoomRedrawTimeoutId !== null) {
      window.clearTimeout(this.zoomRedrawTimeoutId);
      this.zoomRedrawTimeoutId = null;
    }
    this.stopAnimation();
  }

  resetView(): void {
    if (!this.canvasWrapper) {
      return;
    }

    this.canvasWrapper.zoomLevel.set(1);
    this.canvasWrapper.viewportOffset.set({ x: 0, y: 0 });
  }

  zoomIn(): void {
    this.adjustZoom(0.1);
  }

  zoomOut(): void {
    this.adjustZoom(-0.1);
  }

  centerOnNode(nodeId: number): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvasRef().nativeElement;
    const node = this.nodes().find((candidate) => candidate.id === nodeId);
    if (!wrapper || !node) {
      return;
    }

    const zoom = wrapper.zoomLevel();
    wrapper.viewportOffset.set({
      x: canvas.clientWidth / 2 - (node.x + node.width / 2) * zoom,
      y: canvas.clientHeight / 2 - (node.y + node.height / 2) * zoom,
    });
  }

  viewportCenterWorld(): Point | null {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return null;
    }

    const canvas = this.canvasRef().nativeElement;
    const offset = wrapper.viewportOffset();
    const zoom = wrapper.zoomLevel();
    return {
      x: (canvas.clientWidth / 2 - offset.x) / zoom,
      y: (canvas.clientHeight / 2 - offset.y) / zoom,
    };
  }

  protected onMouseMove(event: MouseEvent): void {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return;
    }

    const { canvas, world } = this.getCoordinates(event);
    const { x, y } = canvas;
    const { x: worldX, y: worldY } = world;

    if (this.dragConnection()) {
      const drag = this.dragConnection()!;
      const target = this.getNodeByCoordinates(
        { x: worldX, y: worldY },
        ...this.getNodesConnectedTo(this.getNodeById(drag.from)),
      );

      if (target) {
        const snap = target.inputAnchorSnap();
        if (snap) {
          this.dragConnection.update((current) =>
            current ? { ...current, x: snap.x, y: snap.y, side: 'left' } : current,
          );
          this.hoveredNodeId.set(target.id);
          this.hoveredAnchor.set(target.inputAnchorIndex());
        } else {
          this.dragConnection.update((current) =>
            current ? { ...current, x: worldX, y: worldY, side: undefined } : current,
          );
          this.hoveredNodeId.set(null);
          this.hoveredAnchor.set(null);
        }
      } else {
        this.dragConnection.update((current) =>
          current ? { ...current, x: worldX, y: worldY, side: undefined } : current,
        );
        this.hoveredNodeId.set(null);
        this.hoveredAnchor.set(null);
      }
      return;
    }

    if (this.dragNode()) {
      const drag = this.dragNode()!;
      this.nodeMoved.emit({
        nodeId: drag.id,
        coordinates: {
          x: wrapper.snap(worldX - drag.dx),
          y: wrapper.snap(worldY - drag.dy),
        },
      });
      return;
    }

    if (this.dragOffset()) {
      const previous = this.dragOffset()!;
      wrapper.viewportOffset.update((offset) => ({
        x: offset.x + (x - previous.x),
        y: offset.y + (y - previous.y),
      }));
      this.dragOffset.set({ x, y });
      return;
    }

    const node = this.getNodeByCoordinates({ x: worldX, y: worldY });
    this.hoveredNodeId.set(node ? node.id : null);
    this.hoveredAnchor.set(this.isReadonly() || !node ? null : node.inAnchorCircle(worldX, worldY));
  }

  protected onMouseDown(event: MouseEvent): void {
    const { canvas, world } = this.getCoordinates(event);
    const hovered = this.getNodeByCoordinates(world);

    if (!this.isReadonly() && hovered) {
      const anchor = hovered.inAnchorCircle(world.x, world.y);
      if (anchor !== null && hovered.isOutputAnchor(anchor)) {
        this.dragConnection.set({
          from: hovered.id,
          anchor,
          x: world.x,
          y: world.y,
          side: undefined,
        });
        this.hoveredNodeId.set(hovered.id);
        this.hoveredAnchor.set(anchor);
        this.startAnimation();
        return;
      }

      this.dragNode.set({
        id: hovered.id,
        dx: world.x - hovered.x,
        dy: world.y - hovered.y,
      });
      this.hoveredNodeId.set(hovered.id);
      this.hoveredAnchor.set(anchor);
      this.startAnimation();
      return;
    }

    this.dragOffset.set(canvas);
    this.startAnimation();
  }

  protected onMouseUp(event: MouseEvent): void {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return;
    }

    const { world } = this.getCoordinates(event);
    const drag = this.dragConnection();
    if (drag) {
      const target = this.getNodeByCoordinates(
        world,
        ...this.getNodesConnectedTo(this.getNodeById(drag.from)),
      );
      const snap = target?.inputAnchorSnap();
      if (target && snap) {
        this.connectionCreated.emit({
          sourceNodeId: drag.from,
          destinationNodeId: target.id,
          anchor: drag.anchor,
          side: 'left',
          position: wrapper.snap(snap.pos),
        });
      }
    }

    this.dragConnection.set(null);
    this.dragNode.set(null);
    this.dragOffset.set(null);
    this.hoveredAnchor.set(null);
    this.stopAnimation();
  }

  protected onMouseLeave(): void {
    this.dragConnection.set(null);
    this.dragNode.set(null);
    this.dragOffset.set(null);
    this.hoveredAnchor.set(null);
    this.hoveredNodeId.set(null);
    this.stopAnimation();
  }

  protected onWheel(event: WheelEvent): void {
    event.preventDefault();
    this.adjustZoom(Math.sign(-event.deltaY) * 0.05);
    this.startAnimation();
    if (this.zoomRedrawTimeoutId !== null) {
      window.clearTimeout(this.zoomRedrawTimeoutId);
    }
    this.zoomRedrawTimeoutId = window.setTimeout(() => {
      this.zoomRedrawTimeoutId = null;
      if (!this.dragNode() && !this.dragConnection() && !this.dragOffset()) {
        this.stopAnimation();
      }
    }, 120);
  }

  private isReadonly(): boolean {
    return this.mode() === 'readonly';
  }

  private syncCanvasBackingSize(): void {
    const canvas = this.canvasRef().nativeElement;
    const dpr = window.devicePixelRatio || 1;
    const cssWidth = Math.max(1, Math.floor(canvas.clientWidth));
    const cssHeight = Math.max(1, Math.floor(canvas.clientHeight));
    const width = Math.max(1, Math.floor(cssWidth * dpr));
    const height = Math.max(1, Math.floor(cssHeight * dpr));

    if (canvas.width !== width) {
      canvas.width = width;
    }
    if (canvas.height !== height) {
      canvas.height = height;
    }

    const context = this.canvasWrapper?.ctx;
    if (context) {
      context.setTransform(1, 0, 0, 1, 0, 0);
      context.scale(dpr, dpr);
    }
  }

  private adjustZoom(delta: number): void {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return;
    }

    const nextZoom = Math.min(3, Math.max(0.2, wrapper.zoomLevel() + delta));
    wrapper.zoomLevel.set(Number(nextZoom.toFixed(2)));
  }

  private getCoordinates(event: MouseEvent): { canvas: Point; world: Point } {
    const canvas = this.canvasRef().nativeElement;
    const rect = canvas.getBoundingClientRect();
    const position = {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
    };
    const wrapper = this.canvasWrapper!;
    const offset = wrapper.viewportOffset();
    const zoom = wrapper.zoomLevel();
    return {
      canvas: position,
      world: {
        x: (position.x - offset.x) / zoom,
        y: (position.y - offset.y) / zoom,
      },
    };
  }

  private getNodeById(id: number): NodeModel {
    return this.nodes().find((node) => node.id === id)!;
  }

  private getNodeByCoordinates(coordinates: Point, ...except: NodeModel[]): NodeModel | undefined {
    return this.nodes().find((node) => {
      if (except.includes(node) || except.some((candidate) => candidate.contains(coordinates.x, coordinates.y))) {
        return false;
      }

      return node.contains(coordinates.x, coordinates.y);
    });
  }

  private getNodesConnectedTo(node: NodeModel): NodeModel[] {
    return [...new Set(
      this.connections()
        .filter((connection) => connection.from === node || connection.to === node)
        .flatMap((connection) => [connection.from, connection.to]),
    )];
  }

  private startAnimation(): void {
    if (this.animationId !== null) {
      return;
    }

    const step = () => {
      this.draw();
      this.animationId = requestAnimationFrame(step);
    };
    this.animationId = requestAnimationFrame(step);
  }

  private stopAnimation(): void {
    if (this.animationId === null) {
      return;
    }

    cancelAnimationFrame(this.animationId);
    this.animationId = null;
  }

  private draw(): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvasRef().nativeElement;
    if (!wrapper) {
      return;
    }

    const context = wrapper.ctx;
    wrapper.clear(canvas.width, canvas.height);

    context.save();
    const offset = wrapper.viewportOffset();
    const zoom = wrapper.zoomLevel();
    context.translate(offset.x, offset.y);
    context.scale(zoom, zoom);

    const nodes = this.nodes();
    const connections = this.connections();
    wrapper.drawConnections(connections, nodes);
    wrapper.drawNodes(
      nodes,
      connections,
      this.dragConnection(),
      this.hoveredNodeId(),
      this.hoveredAnchor(),
      { readOnly: this.isReadonly() },
    );

    const drag = this.dragConnection();
    if (drag) {
      const fromNode = this.getNodeById(drag.from);
      if (!fromNode.contains(drag.x, drag.y)) {
        const start = fromNode.anchorPositions()[drag.anchor];
        const hoveredNode = this.hoveredNodeId() && this.hoveredNodeId() !== drag.from
          ? this.getNodeById(this.hoveredNodeId()!)
          : undefined;
        const obstacles = nodes.filter((node) => node.id !== fromNode.id && node.id !== hoveredNode?.id);

        wrapper.drawOrthogonalLine(
          start.x,
          start.y,
          drag.x,
          drag.y,
          obstacles,
          drag.side ?? fromNode.anchorDefinition(drag.anchor)?.side,
          hoveredNode,
          fromNode.anchorDefinition(drag.anchor)?.side,
          fromNode,
        );
      }
    }

    const hoveredNode = this.hoveredNodeId() !== null
      ? nodes.find((node) => node.id === this.hoveredNodeId())
      : null;
    if (hoveredNode?.renderStatus === 'Failed' && hoveredNode.failureMessage?.trim()) {
      wrapper.drawFailureTooltip(hoveredNode, hoveredNode.failureMessage.trim());
    }

    context.restore();
  }
}
