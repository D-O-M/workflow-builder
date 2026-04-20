import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  afterRenderEffect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { ConnectionModel } from '../domain/connection-model';
import { CanvasWrapper } from '../domain/canvas-wrapper';
import { NodeModel, NodeRenderStatus, Point, Side } from '../domain/node-model';

interface PendingClickState {
  pointerId: number;
  startCanvas: Point;
  nodeId: number | null;
  connectionId: string | null;
  shift: boolean;
}

interface DragNodeState {
  pointerId: number;
  startWorld: Point;
  nodeIds: number[];
  initialPositions: Map<number, Point>;
}

interface DragConnectionState {
  pointerId: number;
  from: number;
  anchor: number;
  x: number;
  y: number;
  side?: Side;
}

interface PanState {
  pointerId: number;
  startCanvas: Point;
  initialOffset: Point;
}

interface MarqueeState {
  pointerId: number;
  startWorld: Point;
  currentWorld: Point;
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
  readonly readonlySelectedNodeId = input<number | null>(null);
  readonly readonlyNodeActivated = output<number>();

  private readonly editorStore = inject(BlueprintEditorStore);
  private readonly canvasRef = viewChild<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly gridSize = 20;
  private readonly clickTolerance = 4;

  private canvasWrapper?: CanvasWrapper;
  private resizeObserver?: ResizeObserver;

  protected readonly hoveredNodeId = signal<number | null>(null);
  protected readonly hoveredAnchor = signal<number | null>(null);
  protected readonly pendingClick = signal<PendingClickState | null>(null);
  protected readonly dragNode = signal<DragNodeState | null>(null);
  protected readonly dragConnection = signal<DragConnectionState | null>(null);
  protected readonly panState = signal<PanState | null>(null);
  protected readonly marqueeState = signal<MarqueeState | null>(null);
  readonly zoomPercent = signal(100);

  constructor() {
    afterRenderEffect(() => {
      const wrapper = this.canvasWrapper;
      if (!wrapper) {
        return;
      }

      this.mode();
      this.nodes();
      this.connections();
      this.hoveredNodeId();
      this.hoveredAnchor();
      this.pendingClick();
      this.dragNode();
      this.dragConnection();
      this.panState();
      this.marqueeState();
      this.readonlySelectedNodeId();
      this.editorStore.selectedNodeIds();
      this.editorStore.selectedConnectionIds();
      this.editorStore.canvasFocused();
      wrapper.fontReady();
      wrapper.viewportOffset();
      wrapper.zoomLevel();
      this.zoomPercent.set(Math.round(wrapper.zoomLevel() * 100));
      this.draw();
    });
  }

  ngAfterViewInit(): void {
    const canvas = this.canvasRef()?.nativeElement;
    const context = canvas?.getContext('2d');
    if (!canvas || !context) {
      return;
    }

    this.canvasWrapper = new CanvasWrapper(context, this.gridSize);
    this.syncCanvasBackingSize();

    this.resizeObserver = new ResizeObserver(() => {
      this.syncCanvasBackingSize();
      this.draw();
    });
    this.resizeObserver.observe(canvas);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
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

  focusCanvas(): void {
    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas) {
      return;
    }

    canvas.focus({ preventScroll: true });
    if (!this.isReadonly()) {
      this.editorStore.setCanvasFocused(true);
    }
  }

  centerOnNode(nodeId: number): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvasRef()?.nativeElement;
    const node = this.nodes().find((candidate) => candidate.id === nodeId);
    if (!wrapper || !canvas || !node) {
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
    const canvas = this.canvasRef()?.nativeElement;
    if (!wrapper || !canvas) {
      return null;
    }

    const offset = wrapper.viewportOffset();
    const zoom = wrapper.zoomLevel();
    return {
      x: (canvas.clientWidth / 2 - offset.x) / zoom,
      y: (canvas.clientHeight / 2 - offset.y) / zoom,
    };
  }

  protected onFocus(): void {
    if (!this.isReadonly()) {
      this.editorStore.setCanvasFocused(true);
    }
  }

  protected onBlur(): void {
    if (!this.isReadonly()) {
      this.editorStore.setCanvasFocused(false);
    }
  }

  protected onPointerDown(event: PointerEvent): void {
    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas || !this.canvasWrapper) {
      return;
    }

    this.focusCanvas();
    canvas.setPointerCapture(event.pointerId);

    const { canvas: canvasPoint, world } = this.getCoordinates(event);
    const shift = !this.isReadonly() && this.editorStore.modifiers().shift;
    const node = this.getNodeByCoordinates(world);
    const connection = !this.isReadonly() && !node ? this.getConnectionByCoordinates(world) : undefined;

    if (!this.isReadonly() && node) {
      const anchor = this.getAnchorAt(node, world);
      if (anchor !== null && node.isOutputAnchor(anchor)) {
        this.dragConnection.set({
          pointerId: event.pointerId,
          from: node.id,
          anchor,
          x: world.x,
          y: world.y,
          side: undefined,
        });
        this.hoveredNodeId.set(node.id);
        this.hoveredAnchor.set(anchor);
        return;
      }
    }

    if (!this.isReadonly() && shift && !node && !connection) {
      this.marqueeState.set({
        pointerId: event.pointerId,
        startWorld: world,
        currentWorld: world,
      });
      return;
    }

    this.pendingClick.set({
      pointerId: event.pointerId,
      startCanvas: canvasPoint,
      nodeId: node?.id ?? null,
      connectionId: connection?.id ?? null,
      shift,
    });
  }

  protected onPointerMove(event: PointerEvent): void {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return;
    }

    const { canvas, world } = this.getCoordinates(event);

    const dragConnection = this.dragConnection();
    if (dragConnection?.pointerId === event.pointerId) {
      this.updateConnectionDrag(dragConnection, world);
      return;
    }

    const dragNode = this.dragNode();
    if (dragNode?.pointerId === event.pointerId) {
      this.updateNodeDrag(dragNode, world);
      return;
    }

    const panState = this.panState();
    if (panState?.pointerId === event.pointerId) {
      wrapper.viewportOffset.set({
        x: panState.initialOffset.x + (canvas.x - panState.startCanvas.x),
        y: panState.initialOffset.y + (canvas.y - panState.startCanvas.y),
      });
      return;
    }

    const marqueeState = this.marqueeState();
    if (marqueeState?.pointerId === event.pointerId) {
      this.marqueeState.set({
        ...marqueeState,
        currentWorld: world,
      });
      return;
    }

    const pendingClick = this.pendingClick();
    if (pendingClick?.pointerId === event.pointerId) {
      if (!this.hasMovedBeyondThreshold(pendingClick.startCanvas, canvas)) {
        return;
      }

      if (pendingClick.nodeId !== null && !pendingClick.shift && !this.isReadonly()) {
        this.startNodeDrag(event.pointerId, pendingClick.nodeId, world);
        this.pendingClick.set(null);
        return;
      }

      if (pendingClick.nodeId === null && pendingClick.connectionId === null) {
        this.panState.set({
          pointerId: event.pointerId,
          startCanvas: pendingClick.startCanvas,
          initialOffset: wrapper.viewportOffset(),
        });
        this.pendingClick.set(null);
        return;
      }
    }

    this.updateHoverState(world);
  }

  protected onPointerUp(event: PointerEvent): void {
    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas || !this.canvasWrapper) {
      return;
    }

    this.completePointerInteraction(event, false);
    if (canvas.hasPointerCapture(event.pointerId)) {
      canvas.releasePointerCapture(event.pointerId);
    }
  }

  protected onPointerCancel(event: PointerEvent): void {
    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas || !this.canvasWrapper) {
      return;
    }

    this.completePointerInteraction(event, true);
    if (canvas.hasPointerCapture(event.pointerId)) {
      canvas.releasePointerCapture(event.pointerId);
    }
  }

  protected onPointerLeave(): void {
    if (this.dragConnection() || this.dragNode() || this.panState() || this.marqueeState()) {
      return;
    }

    this.hoveredNodeId.set(null);
    this.hoveredAnchor.set(null);
  }

  protected onWheel(event: WheelEvent): void {
    event.preventDefault();
    this.adjustZoom(Math.sign(-event.deltaY) * 0.05);
  }

  private completePointerInteraction(event: PointerEvent, cancelled: boolean): void {
    const { canvas, world } = this.getCoordinates(event);
    const dragConnection = this.dragConnection();
    if (dragConnection?.pointerId === event.pointerId) {
      if (!cancelled) {
        this.finalizeConnectionDrag(dragConnection, world);
      }

      this.dragConnection.set(null);
      this.hoveredAnchor.set(null);
      this.updateHoverState(world);
      return;
    }

    if (this.dragNode()?.pointerId === event.pointerId) {
      this.dragNode.set(null);
      this.updateHoverState(world);
      return;
    }

    if (this.panState()?.pointerId === event.pointerId) {
      this.panState.set(null);
      this.updateHoverState(world);
      return;
    }

    const marqueeState = this.marqueeState();
    if (marqueeState?.pointerId === event.pointerId) {
      if (!cancelled) {
        const bounds = this.normalizeRect(marqueeState.startWorld, marqueeState.currentWorld);
        this.editorStore.selectNodesWithinRect(
          bounds.left,
          bounds.top,
          bounds.right,
          bounds.bottom,
          true,
        );
      }

      this.marqueeState.set(null);
      this.updateHoverState(world);
      return;
    }

    const pendingClick = this.pendingClick();
    if (pendingClick?.pointerId === event.pointerId) {
      this.pendingClick.set(null);
      if (cancelled || this.hasMovedBeyondThreshold(pendingClick.startCanvas, canvas)) {
        this.updateHoverState(world);
        return;
      }

      this.applyClickSelection(pendingClick);
      this.updateHoverState(world);
      return;
    }

    this.updateHoverState(world);
  }

  private applyClickSelection(click: PendingClickState): void {
    if (this.isReadonly()) {
      if (click.nodeId !== null) {
        this.readonlyNodeActivated.emit(click.nodeId);
      }
      return;
    }

    if (click.nodeId !== null) {
      if (click.shift) {
        this.editorStore.toggleNodeSelection(click.nodeId);
      } else {
        this.editorStore.selectOnlyNode(click.nodeId);
      }
      return;
    }

    if (click.connectionId !== null) {
      if (click.shift) {
        this.editorStore.toggleConnectionSelection(click.connectionId);
      } else {
        this.editorStore.selectOnlyConnection(click.connectionId);
      }
      return;
    }

    this.editorStore.clearSelection();
  }

  private startNodeDrag(pointerId: number, nodeId: number, world: Point): void {
    const selectedNodeIds = this.editorStore.selectedNodeIds();
    const selectedConnectionIds = this.editorStore.selectedConnectionIds();
    const useCurrentSelection = selectedConnectionIds.length === 0 && selectedNodeIds.includes(nodeId);
    const nodeIds = useCurrentSelection ? selectedNodeIds : [nodeId];

    if (!useCurrentSelection) {
      this.editorStore.selectOnlyNode(nodeId);
    }

    const initialPositions = new Map<number, Point>();
    for (const selectedId of nodeIds) {
      const node = this.getNodeById(selectedId);
      if (!node) {
        continue;
      }

      initialPositions.set(selectedId, { x: node.x, y: node.y });
    }

    if (initialPositions.size === 0) {
      return;
    }

    this.editorStore.captureHistory();
    this.dragNode.set({
      pointerId,
      startWorld: world,
      nodeIds: [...initialPositions.keys()],
      initialPositions,
    });
    this.hoveredNodeId.set(nodeId);
  }

  private updateNodeDrag(drag: DragNodeState, world: Point): void {
    const deltaX = world.x - drag.startWorld.x;
    const deltaY = world.y - drag.startWorld.y;
    this.editorStore.updateNodePositions(
      drag.nodeIds.map((nodeId) => {
        const initial = drag.initialPositions.get(nodeId)!;
        return {
          nodeId,
          coordinates: {
            x: initial.x + deltaX,
            y: initial.y + deltaY,
          },
        };
      }),
    );
  }

  private updateConnectionDrag(drag: DragConnectionState, world: Point): void {
    const sourceNode = this.getNodeById(drag.from);
    if (!sourceNode) {
      this.dragConnection.set(null);
      return;
    }

    const target = this.getNodeByCoordinates(
      world,
      ...this.getNodesConnectedTo(sourceNode),
    );

    if (target) {
      const snap = target.inputAnchorSnap();
      if (snap) {
        this.dragConnection.set({
          ...drag,
          x: snap.x,
          y: snap.y,
          side: 'left',
        });
        this.hoveredNodeId.set(target.id);
        this.hoveredAnchor.set(target.inputAnchorIndex());
        return;
      }
    }

    this.dragConnection.set({
      ...drag,
      x: world.x,
      y: world.y,
      side: undefined,
    });
    this.hoveredNodeId.set(null);
    this.hoveredAnchor.set(null);
  }

  private finalizeConnectionDrag(drag: DragConnectionState, world: Point): void {
    const sourceNode = this.getNodeById(drag.from);
    if (!sourceNode) {
      return;
    }

    const target = this.getNodeByCoordinates(
      world,
      ...this.getNodesConnectedTo(sourceNode),
    );
    const snap = target?.inputAnchorSnap();
    if (!target || !snap) {
      return;
    }

    this.editorStore.createConnection(
      drag.from,
      target.id,
      drag.anchor,
      'left',
      snap.pos,
    );
  }

  private updateHoverState(world: Point): void {
    const node = this.getNodeByCoordinates(world);
    this.hoveredNodeId.set(node?.id ?? null);
    this.hoveredAnchor.set(!node || this.isReadonly() ? null : this.getAnchorAt(node, world));
  }

  private isReadonly(): boolean {
    return this.mode() === 'readonly';
  }

  private syncCanvasBackingSize(): void {
    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas) {
      return;
    }

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

  private getCoordinates(event: MouseEvent | PointerEvent): { canvas: Point; world: Point } {
    const canvas = this.canvasRef()?.nativeElement;
    const wrapper = this.canvasWrapper;
    if (!canvas || !wrapper) {
      return {
        canvas: { x: 0, y: 0 },
        world: { x: 0, y: 0 },
      };
    }

    const rect = canvas.getBoundingClientRect();
    const position = {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
    };
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

  private getNodeById(id: number): NodeModel | undefined {
    return this.nodes().find((node) => node.id === id);
  }

  private getNodeByCoordinates(coordinates: Point, ...except: NodeModel[]): NodeModel | undefined {
    return [...this.nodes()].reverse().find((node) => {
      if (except.includes(node) || except.some((candidate) => candidate.contains(coordinates.x, coordinates.y))) {
        return false;
      }

      return node.contains(coordinates.x, coordinates.y);
    });
  }

  private getConnectionByCoordinates(coordinates: Point): ConnectionModel | undefined {
    const tolerance = 10 / this.canvasWrapper!.zoomLevel();
    return [...this.connections()].reverse().find((connection) => {
      const obstacles = this.nodes().filter((node) => node.id !== connection.from.id && node.id !== connection.to.id);
      return connection.hitTest(
        coordinates.x,
        coordinates.y,
        tolerance,
        {
          gridSize: this.gridSize,
          nodeMargin: NodeModel.buffer,
          obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
        },
        obstacles,
      );
    });
  }

  private getNodesConnectedTo(node: NodeModel): NodeModel[] {
    return [...new Set(
      this.connections()
        .filter((connection) => connection.from === node || connection.to === node)
        .flatMap((connection) => [connection.from, connection.to]),
    )];
  }

  private getAnchorAt(node: NodeModel, coordinates: Point): number | null {
    const zoom = this.canvasWrapper?.zoomLevel() ?? 1;
    const extraHitRadius = Math.max(4, 12 / zoom - 7);
    return node.inAnchorCircle(coordinates.x, coordinates.y, extraHitRadius);
  }

  private hasMovedBeyondThreshold(start: Point, current: Point): boolean {
    return Math.abs(current.x - start.x) > this.clickTolerance || Math.abs(current.y - start.y) > this.clickTolerance;
  }

  private normalizeRect(start: Point, end: Point): { left: number; top: number; right: number; bottom: number } {
    return {
      left: Math.min(start.x, end.x),
      top: Math.min(start.y, end.y),
      right: Math.max(start.x, end.x),
      bottom: Math.max(start.y, end.y),
    };
  }

  private draw(): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvasRef()?.nativeElement;
    if (!wrapper || !canvas) {
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
    const selectedNodeIds = this.isReadonly()
      ? new Set<number>(this.readonlySelectedNodeId() !== null ? [this.readonlySelectedNodeId()!] : [])
      : this.editorStore.selectedNodeIdSet();
    const selectedConnectionIds = this.isReadonly()
      ? new Set<string>()
      : this.editorStore.selectedConnectionIdSet();

    wrapper.drawConnections(connections, nodes, selectedConnectionIds);
    wrapper.drawNodes(
      nodes,
      connections,
      this.dragConnection(),
      this.hoveredNodeId(),
      this.hoveredAnchor(),
      selectedNodeIds,
      { readOnly: this.isReadonly() },
    );

    const drag = this.dragConnection();
    if (drag) {
      const fromNode = this.getNodeById(drag.from);
      if (fromNode && !fromNode.contains(drag.x, drag.y)) {
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

    const marquee = this.marqueeState();
    if (marquee) {
      const bounds = this.normalizeRect(marquee.startWorld, marquee.currentWorld);
      wrapper.drawMarqueeRect(bounds.left, bounds.top, bounds.right, bounds.bottom);
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
