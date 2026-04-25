import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  OnDestroy,
  afterRenderEffect,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { buildConnectionMappingSelections, buildConnectionMappingsFromSelections, ConnectionModel } from '../domain/connection-model';
import { CanvasWrapper } from '../domain/canvas-wrapper';
import { NodeModel, Point, Side } from '../domain/node-model';
import { CanvasBackgroundEffect } from '../domain/canvas-backdrop';
import {
  buildCanvasRenderSnapshot,
  CanvasBounds,
  CanvasDetailTier,
  CanvasRenderSnapshot,
  CanvasValidationSeverity,
  RenderedConnection,
  RenderedNode,
} from '../domain/canvas-render-model';
import { BlueprintCanvasStateStore } from '../domain/blueprint-canvas-state.store';

export interface CanvasValidationFocus {
  readonly issueId: string;
  readonly severity: CanvasValidationSeverity;
  readonly nodeId?: number | null;
  readonly connectionId?: string | null;
  readonly routeIndex?: number | null;
  readonly propertyName?: string | null;
  readonly channelName?: string | null;
  readonly side?: Side | null;
  readonly pulseKey: number;
}

interface ResolvedValidationFocus {
  readonly issueId: string;
  readonly severity: CanvasValidationSeverity;
  readonly nodeId: number | null;
  readonly connectionId: string | null;
  readonly routeIndex: number | null;
  readonly sectionKey: string | null;
  readonly portKeys: ReadonlySet<string>;
  readonly bounds: CanvasBounds | null;
}

interface PendingClickState {
  pointerId: number;
  startCanvas: Point;
  startWorld: Point;
  nodeId: number | null;
  settingsNodeId: number | null;
  connectionId: string | null;
  connectionBadgeId: string | null;
  shift: boolean;
  toggleSectionKey: string | null;
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
  portKey?: string | null;
  sourceProperty?: string | null;
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

interface HoveredConnectionRouteState {
  connectionId: string;
  routeIndex: number;
}

interface ConnectionRouteHit {
  connection: RenderedConnection;
  routeIndex: number;
  distance: number;
}

@Component({
  selector: 'app-blueprint-canvas',
  templateUrl: './blueprint-canvas.component.html',
  styleUrl: './blueprint-canvas.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [BlueprintCanvasStateStore],
})
export class BlueprintCanvasComponent implements AfterViewInit, OnDestroy {
  readonly nodes = input.required<NodeModel[]>();
  readonly connections = input.required<ConnectionModel[]>();
  readonly graphRevision = input(0);
  readonly mode = input<'edit' | 'readonly'>('edit');
  readonly backgroundEffect = input<CanvasBackgroundEffect>('plain');
  readonly readonlySelectedNodeId = input<number | null>(null);
  readonly validationFocus = input<CanvasValidationFocus | null>(null);
  readonly readonlyNodeActivated = output<number>();
  readonly nodeSettingsRequested = output<number>();

  private readonly editorStore = inject(BlueprintEditorStore);
  private readonly canvasStateStore = inject(BlueprintCanvasStateStore);
  private readonly canvasRef = viewChild<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly gridSize = 20;
  private readonly clickTolerance = 4;
  private readonly wheelLineStep = 16;
  private readonly wheelDeltaModeLine = 1;
  private readonly wheelDeltaModePage = 2;

  private canvasWrapper?: CanvasWrapper;
  private resizeObserver?: ResizeObserver;
  private pendingDrawFrameId: number | null = null;
  private detailTierTimeoutId: ReturnType<typeof setTimeout> | null = null;

  protected readonly hoveredNodeId = signal<number | null>(null);
  protected readonly hoveredPortKey = signal<string | null>(null);
  protected readonly hoveredConnectionId = signal<string | null>(null);
  protected readonly hoveredConnectionRoute = signal<HoveredConnectionRouteState | null>(null);
  protected readonly backdropHoverCanvasPoint = signal<Point | null>(null);
  protected readonly expandedNodeSections = this.canvasStateStore.expandedNodeSections;
  protected readonly pendingClick = signal<PendingClickState | null>(null);
  protected readonly dragNode = signal<DragNodeState | null>(null);
  protected readonly dragConnection = signal<DragConnectionState | null>(null);
  protected readonly panState = signal<PanState | null>(null);
  protected readonly marqueeState = signal<MarqueeState | null>(null);
  protected readonly transientSimplifiedDetail = signal(false);
  readonly zoomPercent = signal(100);
  private readonly expandedSectionMap = computed<ReadonlyMap<number, ReadonlySet<string>>>(() => {
    const existingNodeIds = new Set(this.nodes().map((node) => node.id));
    return new Map(
      Object.entries(this.expandedNodeSections())
        .map(([nodeId, sectionKeys]) => [Number(nodeId), new Set(sectionKeys)] as const)
        .filter(([nodeId, sectionKeys]) => existingNodeIds.has(nodeId) && sectionKeys.size > 0),
    );
  });
  private readonly detailTier = computed<CanvasDetailTier>(() => {
    const zoom = this.canvasWrapper ? this.canvasWrapper.zoomLevel() : 1;
    if (zoom < 0.85 || this.transientSimplifiedDetail() || this.panState() || this.dragConnection()) {
      return 'simplified';
    }

    return 'full';
  });
  private readonly renderSnapshot = computed<CanvasRenderSnapshot>(() =>
    buildCanvasRenderSnapshot(
      this.nodes(),
      this.connections(),
      this.expandedSectionMap(),
      this.detailTier(),
      this.snapshotPathfinderOptions(),
      this.isReadonly(),
    ));
  private readonly validationRenderSnapshot = computed<CanvasRenderSnapshot>(() =>
    buildCanvasRenderSnapshot(
      this.nodes(),
      this.connections(),
      this.expandedSectionMap(),
      'full',
      this.snapshotPathfinderOptions(),
      this.isReadonly(),
    ));
  readonly routeValidationErrors = computed(() => this.validationRenderSnapshot().routeValidationErrors);
  private readonly resolvedValidationFocus = computed<ResolvedValidationFocus | null>(() =>
    this.resolveValidationFocus(this.renderSnapshot(), this.validationFocus()),
  );

  constructor() {
    effect(() => {
      this.canvasStateStore.resetForGraphRevision(this.graphRevision());
    });

    afterRenderEffect(() => {
      const wrapper = this.canvasWrapper;
      if (!wrapper) {
        return;
      }

      this.mode();
      this.backgroundEffect();
      this.graphRevision();
      this.renderSnapshot();
      this.validationFocus();
      this.resolvedValidationFocus();
      this.hoveredNodeId();
      this.hoveredPortKey();
      this.hoveredConnectionId();
      this.hoveredConnectionRoute();
      this.backdropHoverCanvasPoint();
      this.pendingClick();
      this.dragNode();
      this.dragConnection();
      this.panState();
      this.marqueeState();
      this.transientSimplifiedDetail();
      this.readonlySelectedNodeId();
      this.editorStore.selectedNodeIds();
      this.editorStore.selectedConnectionIds();
      this.editorStore.canvasFocused();
      wrapper.fontReady();
      wrapper.viewportOffset();
      wrapper.zoomLevel();
      this.zoomPercent.set(Math.round(wrapper.zoomLevel() * 100));
      this.scheduleDraw();
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
    this.scheduleDraw();

    this.resizeObserver = new ResizeObserver(() => {
      this.syncCanvasBackingSize();
      this.scheduleDraw();
    });
    this.resizeObserver.observe(canvas);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    if (this.pendingDrawFrameId !== null) {
      cancelAnimationFrame(this.pendingDrawFrameId);
      this.pendingDrawFrameId = null;
    }
    if (this.detailTierTimeoutId !== null) {
      clearTimeout(this.detailTierTimeoutId);
      this.detailTierTimeoutId = null;
    }
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
    const node = this.getNodeById(nodeId);
    if (!wrapper || !canvas || !node) {
      return;
    }

    const zoom = wrapper.zoomLevel();
    wrapper.viewportOffset.set({
      x: canvas.clientWidth / 2 - (node.x + node.width / 2) * zoom,
      y: canvas.clientHeight / 2 - (node.y + node.height / 2) * zoom,
    });
  }

  revealValidationFocus(focus: CanvasValidationFocus | null): void {
    if (!focus) {
      return;
    }

    this.ensureValidationFocusSectionExpanded(focus);
    const resolvedFocus = this.resolveValidationFocus(this.renderSnapshot(), focus);
    if (resolvedFocus?.bounds) {
      this.centerOnBounds(resolvedFocus.bounds);
    } else if (focus.nodeId !== null && focus.nodeId !== undefined) {
      this.centerOnNode(focus.nodeId);
    }

    this.focusCanvas();
    this.scheduleDraw();
  }

  centerOnBounds(bounds: CanvasBounds): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvasRef()?.nativeElement;
    if (!wrapper || !canvas) {
      return;
    }

    const zoom = wrapper.zoomLevel();
    const centerX = (bounds.left + bounds.right) / 2;
    const centerY = (bounds.top + bounds.bottom) / 2;
    wrapper.viewportOffset.set({
      x: canvas.clientWidth / 2 - centerX * zoom,
      y: canvas.clientHeight / 2 - centerY * zoom,
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
    const connectionBadge = !this.isReadonly() && connection
      ? this.getConnectionBadgeByCoordinates(connection, world)
      : undefined;

    const settingsNodeId = !this.isReadonly() && node && this.pointWithinRect(world, node.settingsRect()) ? node.id : null;
    if (settingsNodeId !== null) {
      this.pendingClick.set({
        pointerId: event.pointerId,
        startCanvas: canvasPoint,
        startWorld: world,
        nodeId: node?.id ?? null,
        settingsNodeId,
        connectionId: null,
        connectionBadgeId: null,
        shift,
        toggleSectionKey: null,
      });
      return;
    }

    if (!this.isReadonly() && node) {
      const port = this.getPortAt(node, world);
      if (port && (port.kind === 'output-summary' || port.kind === 'output-property') && port.anchorIndex !== null) {
        this.dragConnection.set({
          pointerId: event.pointerId,
          from: node.id,
          anchor: port.anchorIndex,
          x: world.x,
          y: world.y,
          side: undefined,
          portKey: port.key,
          sourceProperty: port.propertyName,
        });
        this.hoveredNodeId.set(node.id);
        this.hoveredPortKey.set(port.key);
        this.hoveredConnectionId.set(null);
        this.hoveredConnectionRoute.set(null);
        return;
      }
    }

    const toggleSectionKey = node?.sectionToggleHit(world.x, world.y) ?? node?.sectionHeaderHit(world.x, world.y) ?? null;
    if (node && toggleSectionKey) {
      this.pendingClick.set({
        pointerId: event.pointerId,
        startCanvas: canvasPoint,
        startWorld: world,
        nodeId: node.id,
        settingsNodeId: null,
        connectionId: null,
        connectionBadgeId: null,
        shift,
        toggleSectionKey,
      });
      return;
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
      startWorld: world,
      nodeId: node?.id ?? null,
      settingsNodeId: null,
      connectionId: connection?.id ?? null,
      connectionBadgeId: connectionBadge?.id ?? null,
      shift,
      toggleSectionKey: null,
    });
  }

  protected onPointerMove(event: PointerEvent): void {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return;
    }

    const { canvas, world } = this.getCoordinates(event);
    this.backdropHoverCanvasPoint.set(canvas);

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
      if (pendingClick.settingsNodeId !== null || pendingClick.toggleSectionKey !== null) {
        return;
      }

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
    this.backdropHoverCanvasPoint.set(null);

    if (this.dragConnection() || this.dragNode() || this.panState() || this.marqueeState()) {
      return;
    }

    this.hoveredNodeId.set(null);
    this.hoveredPortKey.set(null);
    this.hoveredConnectionId.set(null);
    this.hoveredConnectionRoute.set(null);
  }

  protected onWheel(event: WheelEvent): void {
    const wrapper = this.canvasWrapper;
    if (!wrapper) {
      return;
    }

    event.preventDefault();
    this.pulseSimplifiedDetailTier();
    const delta = this.normalizeWheelDelta(event);
    if (this.shouldZoomFromWheel(event)) {
      const dominantDelta = Math.abs(delta.y) >= Math.abs(delta.x) ? delta.y : delta.x;
      if (dominantDelta !== 0) {
        this.adjustZoom(Math.sign(-dominantDelta) * 0.05, this.getCoordinates(event).canvas);
      }
      return;
    }

    wrapper.viewportOffset.update((currentOffset) => ({
      x: currentOffset.x - delta.x,
      y: currentOffset.y - delta.y,
    }));
  }

  private completePointerInteraction(event: PointerEvent, cancelled: boolean): void {
    const { canvas, world } = this.getCoordinates(event);
    const dragConnection = this.dragConnection();
    if (dragConnection?.pointerId === event.pointerId) {
      if (!cancelled) {
        this.finalizeConnectionDrag(dragConnection, world);
      }

      this.dragConnection.set(null);
      this.hoveredPortKey.set(null);
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
    if (click.toggleSectionKey && click.nodeId !== null) {
      this.toggleNodeSectionExpanded(click.nodeId, click.toggleSectionKey);
      return;
    }

    if (click.settingsNodeId !== null) {
      this.editorStore.selectOnlyNode(click.settingsNodeId);
      this.nodeSettingsRequested.emit(click.settingsNodeId);
      return;
    }

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
      sourceNode,
      ...this.getNodesConnectedTo(sourceNode),
    );

    if (target) {
      const targetPort = this.getInputPortAt(target, world);
      if (targetPort) {
        this.dragConnection.set({
          ...drag,
          x: targetPort.x,
          y: targetPort.y,
          side: targetPort.side,
        });
        this.hoveredNodeId.set(target.id);
        this.hoveredPortKey.set(targetPort.key);
        this.hoveredConnectionId.set(null);
        this.hoveredConnectionRoute.set(null);
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
    this.hoveredPortKey.set(null);
    this.hoveredConnectionId.set(null);
    this.hoveredConnectionRoute.set(null);
  }

  private finalizeConnectionDrag(drag: DragConnectionState, world: Point): void {
    const sourceNode = this.getNodeById(drag.from);
    if (!sourceNode) {
      return;
    }

    const target = this.getNodeByCoordinates(
      world,
      sourceNode,
      ...this.getNodesConnectedTo(sourceNode),
    );
    const targetPort = target ? this.getInputPortAt(target, world) : null;
    const snap = target?.inputAnchorSnap();
    if (!target || !targetPort || !snap) {
      return;
    }

    const connection = this.editorStore.createConnection(
      drag.from,
      target.id,
      drag.anchor,
      'left',
      snap.pos,
    );

    if (!connection) {
      return;
    }

    if (drag.sourceProperty && targetPort.kind === 'input-property' && targetPort.propertyName) {
      const nextSelections = buildConnectionMappingSelections(connection);
      nextSelections[targetPort.propertyName] = drag.sourceProperty;
      this.editorStore.updateConnectionPropertyMappings(
        connection.id,
        buildConnectionMappingsFromSelections(nextSelections),
      );
      return;
    }

    if ((drag.sourceProperty && targetPort.kind === 'input-summary') ||
      (!drag.sourceProperty && targetPort.kind === 'input-property')) {
      this.editorStore.selectOnlyConnection(connection.id);
    }
  }

  private updateHoverState(world: Point): void {
    const node = this.getNodeByCoordinates(world);
    const connectionHit = !node && !this.isReadonly()
      ? this.getConnectionRouteByCoordinates(world)
      : undefined;

    this.hoveredNodeId.set(node?.id ?? null);
    this.hoveredPortKey.set(!node || this.isReadonly() ? null : this.getPortAt(node, world)?.key ?? null);
    this.hoveredConnectionId.set(connectionHit?.connection.id ?? null);
    this.hoveredConnectionRoute.set(connectionHit
      ? { connectionId: connectionHit.connection.id, routeIndex: connectionHit.routeIndex }
      : null);
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

  private adjustZoom(delta: number, focalCanvasPoint?: Point): void {
    const wrapper = this.canvasWrapper;
    const canvas = this.canvasRef()?.nativeElement;
    if (!wrapper) {
      return;
    }

    this.pulseSimplifiedDetailTier();

    const currentZoom = wrapper.zoomLevel();
    const nextZoom = Math.min(3, Math.max(0.2, currentZoom + delta));
    if (Number(nextZoom.toFixed(2)) === currentZoom) {
      return;
    }

    const offset = wrapper.viewportOffset();
    const focus = focalCanvasPoint ?? (
      canvas
        ? { x: canvas.clientWidth / 2, y: canvas.clientHeight / 2 }
        : { x: 0, y: 0 }
    );
    const worldFocus = {
      x: (focus.x - offset.x) / currentZoom,
      y: (focus.y - offset.y) / currentZoom,
    };

    wrapper.zoomLevel.set(Number(nextZoom.toFixed(2)));
    wrapper.viewportOffset.set({
      x: focus.x - worldFocus.x * nextZoom,
      y: focus.y - worldFocus.y * nextZoom,
    });
  }

  private shouldZoomFromWheel(event: WheelEvent): boolean {
    return event.ctrlKey || this.editorStore.modifiers().space;
  }

  private normalizeWheelDelta(event: WheelEvent): Point {
    if (event.deltaMode === this.wheelDeltaModeLine) {
      return {
        x: event.deltaX * this.wheelLineStep,
        y: event.deltaY * this.wheelLineStep,
      };
    }

    if (event.deltaMode === this.wheelDeltaModePage) {
      const canvas = this.canvasRef()?.nativeElement;
      const pageStep = Math.max(canvas?.clientHeight ?? 1, canvas?.clientWidth ?? 1);
      return {
        x: event.deltaX * pageStep,
        y: event.deltaY * pageStep,
      };
    }

    return {
      x: event.deltaX,
      y: event.deltaY,
    };
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

  private getNodeById(id: number): RenderedNode | undefined {
    return this.renderSnapshot().nodeById.get(id);
  }

  private getNodeByCoordinates(coordinates: Point, ...except: RenderedNode[]): RenderedNode | undefined {
    const exceptIds = new Set(except.map((node) => node.id));
    return [...this.renderSnapshot().queryNodesAtPoint(coordinates.x, coordinates.y, exceptIds)]
      .reverse()
      .find((node) => node.containsInteractionPoint(coordinates.x, coordinates.y));
  }

  private getConnectionByCoordinates(coordinates: Point): RenderedConnection | undefined {
    return this.getConnectionRouteByCoordinates(coordinates)?.connection;
  }

  private getConnectionRouteByCoordinates(coordinates: Point): ConnectionRouteHit | undefined {
    const tolerance = 10 / this.canvasWrapper!.zoomLevel();
    let nearestHit: ConnectionRouteHit | undefined;
    let nearestDistance = Number.POSITIVE_INFINITY;
    for (const connection of this.renderSnapshot().queryConnectionsAtPoint(coordinates.x, coordinates.y)) {
      const routeHit = connection.nearestVisualRoute(coordinates.x, coordinates.y, tolerance);
      if (routeHit && routeHit.distance <= tolerance && routeHit.distance < nearestDistance) {
        nearestHit = {
          connection,
          routeIndex: routeHit.routeIndex,
          distance: routeHit.distance,
        };
        nearestDistance = routeHit.distance;
      }
    }

    return nearestHit;
  }

  private getConnectionBadgeByCoordinates(connection: RenderedConnection, coordinates: Point): RenderedConnection | undefined {
    const badgeRadius = 18 / this.canvasWrapper!.zoomLevel();
    return connection.badgeHitTest(coordinates.x, coordinates.y, badgeRadius)
      ? connection
      : undefined;
  }

  private getNodesConnectedTo(node: RenderedNode): RenderedNode[] {
    return this.renderSnapshot().getConnectedNodes(node.id);
  }

  private getBlockedTargetNodeIds(snapshot: CanvasRenderSnapshot, sourceNodeId: number): ReadonlySet<number> {
    return new Set([sourceNodeId, ...snapshot.getConnectedNodeIds(sourceNodeId)]);
  }

  private getPortAt(node: RenderedNode, coordinates: Point) {
    const zoom = this.canvasWrapper?.zoomLevel() ?? 1;
    const extraHitRadius = Math.max(4, 12 / zoom - 7);
    return node.portHit(coordinates.x, coordinates.y, extraHitRadius);
  }

  private getInputPortAt(node: RenderedNode, coordinates: Point) {
    const port = this.getPortAt(node, coordinates);
    if (port && (port.kind === 'input-summary' || port.kind === 'input-property')) {
      return port;
    }

    return node.inputSummaryPort();
  }

  private pointWithinRect(point: Point, rect: { x: number; y: number; width: number; height: number }): boolean {
    return (
      point.x >= rect.x &&
      point.x <= rect.x + rect.width &&
      point.y >= rect.y &&
      point.y <= rect.y + rect.height
    );
  }

  private toggleNodeSectionExpanded(nodeId: number, sectionKey: string): void {
    this.canvasStateStore.toggleNodeSectionExpanded(nodeId, sectionKey);
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

  private scheduleDraw(): void {
    if (this.pendingDrawFrameId !== null) {
      return;
    }

    const draw = () => {
      this.pendingDrawFrameId = null;
      this.draw();
    };

    if (typeof requestAnimationFrame === 'function') {
      this.pendingDrawFrameId = requestAnimationFrame(draw);
      return;
    }

    queueMicrotask(draw);
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
    wrapper.drawBackdrop(
      canvas.clientWidth,
      canvas.clientHeight,
      this.backgroundEffect(),
      this.backdropHoverCanvasPoint(),
    );

    const renderedGraph = this.renderSnapshot();
    const nodes = renderedGraph.nodes;
    const connections = renderedGraph.connections;
    const selectedNodeIds = this.isReadonly()
      ? new Set<number>(this.readonlySelectedNodeId() !== null ? [this.readonlySelectedNodeId()!] : [])
      : this.editorStore.selectedNodeIdSet();
    const selectedConnectionIds = this.isReadonly()
      ? new Set<string>()
      : this.editorStore.selectedConnectionIdSet();
    const channelFocusedConnectionIds = this.channelFocusedConnectionIds(
      renderedGraph,
      this.hoveredNodeId(),
      this.hoveredPortKey(),
    );
    const validationFocus = this.resolvedValidationFocus();
    const focusedPortKeys = this.focusedEndpointPortKeys(
      renderedGraph,
      selectedConnectionIds,
      this.hoveredConnectionRoute(),
      this.hoveredConnectionId(),
      channelFocusedConnectionIds,
      validationFocus,
    );

    wrapper.drawConnections(connections, selectedConnectionIds, {
      readOnly: this.isReadonly(),
      detailTier: renderedGraph.detailTier,
      hoveredConnectionId: this.hoveredConnectionId(),
      hoveredConnectionRouteIndex: this.hoveredConnectionRoute()?.routeIndex ?? null,
      channelFocusedConnectionIds,
      validationConnectionFocus: validationFocus?.connectionId
        ? {
          connectionId: validationFocus.connectionId,
          routeIndex: validationFocus.routeIndex,
          severity: validationFocus.severity,
        }
        : null,
    });
    wrapper.drawNodes(
      nodes,
      renderedGraph.portConnectivity,
      this.dragConnection(),
      this.hoveredNodeId(),
      this.hoveredPortKey(),
      selectedNodeIds,
      {
        readOnly: this.isReadonly(),
        detailTier: renderedGraph.detailTier,
        blockedTargetNodeIds: this.dragConnection()
          ? this.getBlockedTargetNodeIds(renderedGraph, this.dragConnection()!.from)
          : undefined,
        focusedPortKeys,
        validationFocus: validationFocus
          ? {
            severity: validationFocus.severity,
            nodeId: validationFocus.nodeId,
            sectionKey: validationFocus.sectionKey,
            portKeys: validationFocus.portKeys,
          }
          : null,
      },
    );

    const drag = this.dragConnection();
    if (drag) {
      const fromNode = this.getNodeById(drag.from);
      if (fromNode && !fromNode.contains(drag.x, drag.y)) {
        const startPort = drag.portKey ? fromNode.portDefinition(drag.portKey) : fromNode.outputSummaryPort(drag.anchor);
        const hoveredNode = this.hoveredNodeId() && this.hoveredNodeId() !== drag.from
          ? this.getNodeById(this.hoveredNodeId()!)
          : undefined;
        const obstacles = nodes.filter((node) => node.id !== fromNode.id && node.id !== hoveredNode?.id);

        if (!startPort) {
          context.restore();
          return;
        }

        wrapper.drawOrthogonalLine(
          startPort.x,
          startPort.y,
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

  private snapshotPathfinderOptions() {
    return {
      gridSize: this.gridSize,
      nodeMargin: NodeModel.buffer,
      obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
    };
  }

  private ensureValidationFocusSectionExpanded(focus: CanvasValidationFocus): void {
    if (focus.nodeId === null || focus.nodeId === undefined || !focus.propertyName || !focus.channelName) {
      return;
    }

    const node = this.renderSnapshot().nodeById.get(focus.nodeId);
    const sectionKey = node ? this.sectionKeyForValidationFocus(node, focus) : null;
    if (!sectionKey) {
      return;
    }

    this.canvasStateStore.ensureNodeSectionExpanded(focus.nodeId, sectionKey);
  }

  private resolveValidationFocus(
    snapshot: CanvasRenderSnapshot,
    focus: CanvasValidationFocus | null,
  ): ResolvedValidationFocus | null {
    if (!focus) {
      return null;
    }

    const portKeys = new Set<string>();
    let bounds: CanvasBounds | null = null;
    let nodeId = focus.nodeId ?? null;
    let connectionId = focus.connectionId ?? null;
    let routeIndex = focus.routeIndex ?? null;
    let sectionKey: string | null = null;

    if (connectionId) {
      const connection = snapshot.connectionById.get(connectionId);
      if (connection) {
        const route = routeIndex !== null ? connection.routes[routeIndex] ?? null : null;
        bounds = this.unionCanvasBounds(bounds, route?.bounds ?? connection.routeBounds);
        if (route) {
          portKeys.add(this.portFocusKey(connection.from, route.startPortKey));
          portKeys.add(this.portFocusKey(connection.to, route.endPortKey));
        } else {
          const sourceSummaryPort = connection.from.outputSummaryPort(connection.anchor);
          const targetSummaryPort = connection.to.inputSummaryPort();
          if (sourceSummaryPort) {
            portKeys.add(this.portFocusKey(connection.from, sourceSummaryPort.key));
          }
          if (targetSummaryPort) {
            portKeys.add(this.portFocusKey(connection.to, targetSummaryPort.key));
          }
        }
      } else {
        connectionId = null;
        routeIndex = null;
      }
    }

    if (nodeId !== null) {
      const node = snapshot.nodeById.get(nodeId);
      if (node) {
        const nodeTarget = this.resolveValidationNodeTarget(node, focus);
        bounds = this.unionCanvasBounds(bounds, nodeTarget.bounds);
        sectionKey = nodeTarget.sectionKey;
        for (const portKey of nodeTarget.portKeys) {
          portKeys.add(portKey);
        }
      } else {
        nodeId = null;
      }
    }

    if (nodeId === null && connectionId === null) {
      return null;
    }

    return {
      issueId: focus.issueId,
      severity: focus.severity,
      nodeId,
      connectionId,
      routeIndex,
      sectionKey,
      portKeys,
      bounds,
    };
  }

  private resolveValidationNodeTarget(
    node: RenderedNode,
    focus: CanvasValidationFocus,
  ): { bounds: CanvasBounds | null; sectionKey: string | null; portKeys: ReadonlySet<string> } {
    const portKeys = new Set<string>();
    const section = this.sectionForValidationFocus(node, focus);
    if (!section) {
      return {
        bounds: this.nodeBounds(node),
        sectionKey: null,
        portKeys,
      };
    }

    let bounds: CanvasBounds | null = this.sectionBounds(node, section);
    const targetRow = focus.propertyName
      ? section.propertyRows.find((row) => row.name === focus.propertyName) ?? null
      : null;

    if (targetRow) {
      bounds = this.unionCanvasBounds(bounds, this.propertyRowBounds(node, targetRow));
      portKeys.add(this.portFocusKey(node, targetRow.port.key));
    } else {
      portKeys.add(this.portFocusKey(node, section.summaryPort.key));
    }

    return {
      bounds,
      sectionKey: section.key,
      portKeys,
    };
  }

  private sectionForValidationFocus(node: RenderedNode, focus: CanvasValidationFocus) {
    const sectionKey = this.sectionKeyForValidationFocus(node, focus);
    if (!sectionKey) {
      return null;
    }

    if (node.layout.inputSection?.key === sectionKey) {
      return node.layout.inputSection;
    }

    return node.layout.outputSections.find((section) => section.key === sectionKey) ?? null;
  }

  private sectionKeyForValidationFocus(node: RenderedNode, focus: CanvasValidationFocus): string | null {
    if (!focus.channelName) {
      return null;
    }

    if (focus.side === 'left' || (!focus.side && node.inputChannelName === focus.channelName)) {
      return node.inputChannelName === focus.channelName ? `left:${focus.channelName}` : null;
    }

    if (focus.side === 'right' || !focus.side) {
      return node.outputChannelNames.includes(focus.channelName) ? `right:${focus.channelName}` : null;
    }

    return null;
  }

  private nodeBounds(node: RenderedNode): CanvasBounds {
    return {
      left: node.x,
      top: node.y,
      right: node.x + node.width,
      bottom: node.y + node.height,
    };
  }

  private sectionBounds(node: RenderedNode, section: NonNullable<RenderedNode['layout']['inputSection']>): CanvasBounds {
    const portBounds = this.portBounds(section.summaryPort);
    return this.unionCanvasBounds(
      {
        left: node.x + 14,
        top: section.headerTop,
        right: node.x + node.width - 14,
        bottom: section.bottom,
      },
      portBounds,
    )!;
  }

  private propertyRowBounds(
    node: RenderedNode,
    row: NonNullable<RenderedNode['layout']['inputSection']>['propertyRows'][number],
  ): CanvasBounds {
    return this.unionCanvasBounds(
      {
        left: node.x + 18,
        top: row.top,
        right: node.x + node.width - 18,
        bottom: row.top + row.height,
      },
      this.portBounds(row.port),
    )!;
  }

  private portBounds(port: NonNullable<RenderedNode['layout']['inputSection']>['summaryPort']): CanvasBounds {
    const rect = port.hitBounds ?? port.visualBounds;
    if (rect) {
      return {
        left: rect.x,
        top: rect.y,
        right: rect.x + rect.width,
        bottom: rect.y + rect.height,
      };
    }

    return {
      left: port.x - port.radius,
      top: port.y - port.radius,
      right: port.x + port.radius,
      bottom: port.y + port.radius,
    };
  }

  private unionCanvasBounds(left: CanvasBounds | null, right: CanvasBounds | null): CanvasBounds | null {
    if (!left) {
      return right;
    }

    if (!right) {
      return left;
    }

    return {
      left: Math.min(left.left, right.left),
      top: Math.min(left.top, right.top),
      right: Math.max(left.right, right.right),
      bottom: Math.max(left.bottom, right.bottom),
    };
  }

  private focusedEndpointPortKeys(
    snapshot: CanvasRenderSnapshot,
    selectedConnectionIds: ReadonlySet<string>,
    hoveredConnectionRoute: HoveredConnectionRouteState | null,
    hoveredConnectionId: string | null,
    channelFocusedConnectionIds: ReadonlySet<string> = new Set<string>(),
    validationFocus: ResolvedValidationFocus | null = null,
  ): ReadonlySet<string> {
    const focusedConnectionIds = new Set(
      [...selectedConnectionIds].filter((connectionId) => connectionId !== hoveredConnectionRoute?.connectionId),
    );
    if (hoveredConnectionId && !hoveredConnectionRoute) {
      focusedConnectionIds.add(hoveredConnectionId);
    }
    for (const connectionId of channelFocusedConnectionIds) {
      focusedConnectionIds.add(connectionId);
    }
    if (validationFocus?.connectionId) {
      focusedConnectionIds.add(validationFocus.connectionId);
    }

    if (focusedConnectionIds.size === 0 && !validationFocus) {
      return new Set();
    }

    const portKeys = new Set(validationFocus?.portKeys ?? []);
    if (hoveredConnectionRoute) {
      this.addRouteEndpointPortKeys(snapshot, hoveredConnectionRoute, portKeys);
    }

    for (const connectionId of focusedConnectionIds) {
      const connection = snapshot.connectionById.get(connectionId);
      if (!connection) {
        continue;
      }

      const sourceSummaryPort = connection.from.outputSummaryPort(connection.anchor);
      if (sourceSummaryPort) {
        portKeys.add(this.portFocusKey(connection.from, sourceSummaryPort.key));
      }

      const targetSummaryPort = connection.to.inputSummaryPort();
      if (targetSummaryPort) {
        portKeys.add(this.portFocusKey(connection.to, targetSummaryPort.key));
      }

      for (const mapping of connection.effectiveMappings) {
        const sourcePropertyPort = connection.from.outputPropertyPort(connection.anchor, mapping.sourceProperty);
        if (sourcePropertyPort) {
          portKeys.add(this.portFocusKey(connection.from, sourcePropertyPort.key));
        }

        const targetPropertyPort = connection.to.inputPropertyPort(mapping.targetProperty);
        if (targetPropertyPort) {
          portKeys.add(this.portFocusKey(connection.to, targetPropertyPort.key));
        }
      }
    }

    return portKeys;
  }

  private addRouteEndpointPortKeys(
    snapshot: CanvasRenderSnapshot,
    hoveredConnectionRoute: HoveredConnectionRouteState,
    portKeys: Set<string>,
  ): void {
    const connection = snapshot.connectionById.get(hoveredConnectionRoute.connectionId);
    const route = connection?.routes[hoveredConnectionRoute.routeIndex];
    if (!connection || !route) {
      return;
    }

    portKeys.add(this.portFocusKey(connection.from, route.startPortKey));
    portKeys.add(this.portFocusKey(connection.to, route.endPortKey));
  }

  private portFocusKey(node: RenderedNode, portKey: string): string {
    return `${node.id}:${portKey}`;
  }

  private channelFocusedConnectionIds(
    snapshot: CanvasRenderSnapshot,
    hoveredNodeId: number | null,
    hoveredPortKey: string | null,
  ): ReadonlySet<string> {
    if (hoveredNodeId === null || !hoveredPortKey) {
      return new Set();
    }

    const node = snapshot.nodeById.get(hoveredNodeId);
    const port = node?.portDefinition(hoveredPortKey);
    if (!node || !port || (port.kind !== 'input-summary' && port.kind !== 'output-summary')) {
      return new Set();
    }

    const section = node.sectionForPort(port);
    if (!section?.expanded) {
      return new Set();
    }

    const connectionIds = new Set<string>();
    for (const connection of snapshot.connections) {
      if (port.kind === 'output-summary' && port.anchorIndex !== null) {
        if (connection.from.id === node.id && connection.anchor === port.anchorIndex) {
          connectionIds.add(connection.id);
        }
        continue;
      }

      if (port.kind === 'input-summary' && connection.to.id === node.id) {
        connectionIds.add(connection.id);
      }
    }

    return connectionIds;
  }

  private pulseSimplifiedDetailTier(): void {
    this.transientSimplifiedDetail.set(true);
    if (this.detailTierTimeoutId !== null) {
      clearTimeout(this.detailTierTimeoutId);
    }

    this.detailTierTimeoutId = setTimeout(() => {
      this.transientSimplifiedDetail.set(false);
      this.detailTierTimeoutId = null;
    }, 140);
  }
}
