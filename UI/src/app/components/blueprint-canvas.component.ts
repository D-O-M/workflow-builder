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
import {
    CanvasInputPropertyHit,
    CanvasOutputPropertyDragEvent,
    CanvasPropertyClientPoint,
    createCanvasInputPropertyHit,
    createCanvasOutputPropertyDragEvent,
} from '../domain/canvas-bridge-adapter';
import { getVisibleWorldBounds } from '../domain/canvas-backdrop';
import {
    canvasModeCapabilities,
    canStartCanvasConnectionFrom,
    isPassiveCanvasSummaryPort,
} from '../domain/canvas-mode-policy';
import {
    CanvasConnectionRouteHit,
    CanvasHitTester,
} from '../domain/canvas-hit-tester';
import {
    CanvasDragConnectionState,
    CanvasDragNodeState,
    CanvasInteractionController,
    CanvasInteractionEffect,
    CanvasPendingClickState,
} from '../domain/canvas-interaction-controller';
import {
    createCanvasRenderCullPlan,
    CanvasRenderCullPlan,
} from '../domain/canvas-render-culling';
import { CanvasConnectionRouteCache } from '../domain/canvas-render-route-cache';
import {
    CanvasRenderFrameFactory,
    CanvasRenderFramePlan,
} from '../domain/canvas-render-frame-factory';
import {
    CanvasRendererHost,
    MainThreadCanvasRendererHost,
    OffscreenCanvasRendererHost,
} from '../domain/canvas-renderer-host';
import { CanvasViewportController } from '../domain/canvas-viewport-controller';
import { CanvasWrapper } from '../domain/canvas-wrapper';
import type { CanvasNodeIssueSummary } from '../domain/canvas-wrapper';
import { NodeModel, Point, Side } from '../domain/node-model';
import {
    buildCanvasRenderSnapshot,
    CanvasBounds,
    CanvasDetailTier,
    CanvasRenderSnapshot,
    RenderedConnection,
    RenderedNode,
} from '../domain/canvas-render-model';
import {
    CanvasResolvedValidationFocus,
    CanvasValidationFocus,
    CanvasValidationFocusResolver,
    CanvasValidationIssueMarker,
} from '../domain/canvas-validation-focus-resolver';
import { BlueprintCanvasStateStore } from '../domain/blueprint-canvas-state.store';
import {
    BlueprintCanvasBridgePort,
    BlueprintCanvasEvent,
    BlueprintCanvasGraph,
    BlueprintCanvasRuntime,
    BlueprintCanvasSelection,
    BlueprintCanvasValidation,
    BlueprintCanvasValidationPort,
    BlueprintCanvasViewportPort,
    DEFAULT_BLUEPRINT_CANVAS_RUNTIME,
    EMPTY_BLUEPRINT_CANVAS_SELECTION,
    EMPTY_BLUEPRINT_CANVAS_VALIDATION,
} from './blueprint-canvas-contract';

export type {
    BlueprintCanvasMode,
    CanvasInteractionEvent,
    CanvasRenderContext,
    CanvasRuntimeConfig,
    CanvasViewportInsets,
    CanvasViewportState,
} from '../domain/canvas-runtime';

export type {
    CanvasInputPropertyHit,
    CanvasOutputPropertyDragEvent,
    CanvasPropertyClientPoint,
} from '../domain/canvas-bridge-adapter';

export type {
    CanvasValidationFocus,
    CanvasValidationIssueMarker,
} from '../domain/canvas-validation-focus-resolver';

export type {
    BlueprintCanvasBridgePort,
    BlueprintCanvasEvent,
    BlueprintCanvasGraph,
    BlueprintCanvasRuntime,
    BlueprintCanvasSelection,
    BlueprintCanvasValidation,
    BlueprintCanvasValidationPort,
    BlueprintCanvasViewportPort,
    CanvasModifierState,
} from './blueprint-canvas-contract';

type ResolvedValidationFocus = CanvasResolvedValidationFocus;
type PendingClickState = CanvasPendingClickState;
type DragNodeState = CanvasDragNodeState;
type DragConnectionState = CanvasDragConnectionState;
type ConnectionRouteHit = CanvasConnectionRouteHit;

interface BlueprintCanvasE2eSnapshot {
    readonly id: string;
    readonly mode: string;
    readonly zoomPercent: number;
    readonly renderer: BlueprintCanvasE2eRendererState;
    readonly canvasRect: {
        readonly left: number;
        readonly top: number;
        readonly width: number;
        readonly height: number;
    };
    readonly viewport: ReturnType<CanvasViewportController['state']>;
    readonly nodes: readonly {
        readonly id: number;
        readonly name: string;
        readonly kind: NodeModel['nodeKind'];
        readonly worldBounds: CanvasBounds;
        readonly clientBounds: CanvasBounds | null;
        readonly ports: readonly {
            readonly key: string;
            readonly kind: string;
            readonly anchorIndex: number | null;
            readonly channelName: string | null;
            readonly propertyName: string | null;
            readonly clientX: number;
            readonly clientY: number;
        }[];
        readonly sections: readonly {
            readonly key: string;
            readonly name: string;
            readonly label: string;
            readonly side: 'left' | 'right';
            readonly expanded: boolean;
            readonly headerBounds: CanvasBounds | null;
            readonly toggleBounds: CanvasBounds | null;
        }[];
    }[];
}

interface BlueprintCanvasE2eHandle {
    readonly snapshot: () => BlueprintCanvasE2eSnapshot | null;
    readonly requestFrame: () => BlueprintCanvasE2eRendererState | null;
    readonly rendererState: () => BlueprintCanvasE2eRendererState | null;
}

interface BlueprintCanvasE2eRendererState {
    readonly kind: CanvasRendererHost['kind'];
    readonly submittedFrameId: number;
    readonly presentedFrameId: number;
    readonly nodeCount: number;
    readonly connectionCount: number;
}

interface WorkflowBuilderE2eApi {
    readonly canvases: Record<string, BlueprintCanvasE2eHandle>;
    getCanvasSnapshot(id: string): BlueprintCanvasE2eSnapshot | null;
}

@Component({
    selector: 'app-blueprint-canvas',
    templateUrl: './blueprint-canvas.component.html',
    styleUrl: './blueprint-canvas.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush,
    providers: [BlueprintCanvasStateStore],
})
export class BlueprintCanvasComponent implements AfterViewInit, OnDestroy {
    readonly canvasId = input('blueprint-canvas');
    readonly graph = input.required<BlueprintCanvasGraph>();
    readonly runtime = input<BlueprintCanvasRuntime>(
        DEFAULT_BLUEPRINT_CANVAS_RUNTIME,
    );
    readonly selection = input<BlueprintCanvasSelection>(
        EMPTY_BLUEPRINT_CANVAS_SELECTION,
    );
    readonly validationConfig = input<BlueprintCanvasValidation>(
        EMPTY_BLUEPRINT_CANVAS_VALIDATION,
        { alias: 'validation' },
    );
    readonly canvasEvent = output<BlueprintCanvasEvent>();

    private readonly canvasStateStore = inject(BlueprintCanvasStateStore);
    private readonly canvasRef =
        viewChild<ElementRef<HTMLCanvasElement>>('canvas');
    private readonly gridSize = 20;
    private readonly clickTolerance = 4;
    private readonly wheelLineStep = 16;
    private readonly wheelDeltaModeLine = 1;
    private readonly wheelDeltaModePage = 2;
    private readonly hitTester = new CanvasHitTester();
    private readonly validationFocusResolver =
        new CanvasValidationFocusResolver();
    private readonly renderFrameFactory = new CanvasRenderFrameFactory(
        this.hitTester,
        this.validationFocusResolver,
    );
    private readonly interactionController = new CanvasInteractionController(
        this.clickTolerance,
    );
    private readonly routeCache = new CanvasConnectionRouteCache();
    private readonly viewportController = new CanvasViewportController(
        () => this.canvasRef()?.nativeElement ?? null,
        () => this.canvasWrapper ?? null,
        () => this.viewportInsets(),
        () => this.notifyViewportChanged(),
    );
    private readonly nodes = computed(() => [...this.graph().nodes]);
    private readonly connections = computed(() => [
        ...this.graph().connections,
    ]);
    private readonly graphRevision = computed(() => this.graph().revision);
    protected readonly mode = computed(() => this.runtime().mode);
    private readonly backgroundEffect = computed(
        () => this.runtime().backgroundEffect,
    );
    private readonly connectionPresentation = computed(
        () => this.runtime().connectionPresentation,
    );
    private readonly viewportInsets = computed(
        () => this.runtime().viewportInsets,
    );
    private readonly readonlySelectedNodeId = computed(
        () => this.selection().readonlySelectedNodeId,
    );
    private readonly selectedNodeIds = computed(
        () => new Set(this.selection().selectedNodeIds),
    );
    private readonly selectedConnectionIds = computed(
        () => new Set(this.selection().selectedConnectionIds),
    );
    private readonly validationFocus = computed(
        () => this.validationConfig().focus,
    );
    private readonly validationIssueMarkers = computed(
        () => this.validationConfig().issueMarkers,
    );

    private canvasWrapper?: CanvasWrapper;
    private rendererHost?: CanvasRendererHost;
    private resizeObserver?: ResizeObserver;
    private canvasBackingSize = {
        width: 1,
        height: 1,
        devicePixelRatio: 1,
    };
    private pendingDrawFrameId: number | null = null;
    private pendingViewportChangedFrameId: number | null = null;
    private pendingViewportChangedUsesAnimationFrame = false;
    private detailTierTimeoutId: ReturnType<typeof setTimeout> | null = null;

    protected readonly hoveredNodeId = this.interactionController.hoveredNodeId;
    protected readonly hoveredNodeIssueNodeId =
        this.interactionController.hoveredNodeIssueNodeId;
    protected readonly hoveredPortKey =
        this.interactionController.hoveredPortKey;
    protected readonly hoveredSectionKey =
        this.interactionController.hoveredSectionKey;
    protected readonly hoveredConnectionId =
        this.interactionController.hoveredConnectionId;
    protected readonly hoveredConnectionRoute =
        this.interactionController.hoveredConnectionRoute;
    protected readonly backdropHoverCanvasPoint =
        this.interactionController.backdropHoverCanvasPoint;
    protected readonly expandedNodeSections =
        this.canvasStateStore.expandedNodeSections;
    protected readonly pendingClick = this.interactionController.pendingClick;
    protected readonly dragNode = this.interactionController.dragNode;
    protected readonly dragConnection =
        this.interactionController.dragConnection;
    protected readonly panState = this.interactionController.panState;
    protected readonly marqueeState = this.interactionController.marqueeState;
    protected readonly transientSimplifiedDetail = signal(false);
    private readonly transientNodePositions = signal<ReadonlyMap<
        number,
        Point
    > | null>(null);
    readonly zoomPercent = signal(100);
    readonly viewport: BlueprintCanvasViewportPort = {
        reset: () => this.resetViewport(),
        zoomIn: () => this.zoomViewportIn(),
        zoomOut: () => this.zoomViewportOut(),
        focus: () => this.focusViewport(),
        centerOnNode: (nodeId) => this.centerViewportOnNode(nodeId),
        centerOnBounds: (bounds) => this.centerViewportOnBounds(bounds),
        centerWorld: () => this.viewportCenterWorld(),
        containsClientPoint: (point, tolerance = 0) =>
            this.viewportContainsClientPoint(point, tolerance),
    };
    readonly validation: BlueprintCanvasValidationPort = {
        reveal: (focus) => this.revealValidationFocus(focus),
        routeErrors: () =>
            this.validationRenderSnapshot().routeValidationErrors,
    };
    readonly bridge: BlueprintCanvasBridgePort = {
        outputPropertyClientPoint: (nodeId, channelName, propertyName) =>
            this.outputPropertyClientPoint(nodeId, channelName, propertyName),
        inputPropertyClientPoint: (nodeId, propertyName) =>
            this.inputPropertyClientPoint(nodeId, propertyName),
        hitTestInputPropertyAtClientPoint: (clientX, clientY) =>
            this.hitTestInputPropertyAtClientPoint(clientX, clientY),
    };
    private readonly modeCapabilities = computed(() =>
        canvasModeCapabilities(this.mode()),
    );
    private readonly expandedSectionMap = computed<
        ReadonlyMap<number, ReadonlySet<string>>
    >(() => {
        const existingNodeIds = new Set(this.nodes().map((node) => node.id));
        return new Map(
            Object.entries(this.expandedNodeSections())
                .map(
                    ([nodeId, sectionKeys]) =>
                        [Number(nodeId), new Set(sectionKeys)] as const,
                )
                .filter(
                    ([nodeId, sectionKeys]) =>
                        existingNodeIds.has(nodeId) && sectionKeys.size > 0,
                ),
        );
    });
    private readonly detailTier = computed<CanvasDetailTier>(() => {
        const zoom = this.canvasWrapper ? this.canvasWrapper.zoomLevel() : 1;
        if (
            zoom < 0.85 ||
            this.transientSimplifiedDetail() ||
            this.panState() ||
            this.dragConnection()
        ) {
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
            {
                transientNodePositions: this.transientNodePositions(),
                routeCache: this.routeCache,
            },
        ),
    );
    private readonly validationRenderSnapshot = computed<CanvasRenderSnapshot>(
        () =>
            buildCanvasRenderSnapshot(
                this.nodes(),
                this.connections(),
                this.expandedSectionMap(),
                'full',
                this.snapshotPathfinderOptions(),
                this.isReadonly(),
                {
                    routeCache: this.routeCache,
                },
            ),
    );
    private readonly resolvedValidationFocus =
        computed<ResolvedValidationFocus | null>(() =>
            this.validationFocusResolver.resolveValidationFocus({
                snapshot: this.renderSnapshot(),
                focus: this.validationFocus(),
            }),
        );
    private readonly nodeIssueSummaries = computed<
        ReadonlyMap<number, CanvasNodeIssueSummary>
    >(() =>
        this.validationFocusResolver.buildNodeIssueSummaries(
            this.validationIssueMarkers(),
        ),
    );

    constructor() {
        effect(() => {
            this.canvasStateStore.resetForGraphRevision(this.graphRevision());
            this.routeCache.clear();
            this.transientNodePositions.set(null);
        });

        afterRenderEffect(() => {
            const wrapper = this.canvasWrapper;
            if (!wrapper) {
                return;
            }

            this.mode();
            this.modeCapabilities();
            this.backgroundEffect();
            this.graphRevision();
            this.renderSnapshot();
            this.validationFocus();
            this.validationIssueMarkers();
            this.viewportInsets();
            this.resolvedValidationFocus();
            this.nodeIssueSummaries();
            this.hoveredNodeId();
            this.hoveredNodeIssueNodeId();
            this.hoveredPortKey();
            this.hoveredSectionKey();
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
            this.selection();
            this.selectedNodeIds();
            this.selectedConnectionIds();
            wrapper.fontReady();
            this.rendererHost?.updateFontState(wrapper.fontState());
            wrapper.viewportOffset();
            wrapper.zoomLevel();
            this.zoomPercent.set(Math.round(wrapper.zoomLevel() * 100));
            this.scheduleDraw();
        });
    }

    ngAfterViewInit(): void {
        const canvas = this.canvasRef()?.nativeElement;
        if (!canvas) {
            return;
        }

        this.initializeCanvasRenderer(canvas);
        if (!this.canvasWrapper || !this.rendererHost) {
            return;
        }

        this.syncCanvasBackingSize();
        this.scheduleDraw();
        this.registerE2eCanvas();

        this.resizeObserver = new ResizeObserver(() => {
            this.syncCanvasBackingSize();
            this.scheduleDraw();
            this.notifyViewportChanged();
        });
        this.resizeObserver.observe(canvas);
    }

    ngOnDestroy(): void {
        this.unregisterE2eCanvas();
        this.resizeObserver?.disconnect();
        this.rendererHost?.destroy();
        this.rendererHost = undefined;
        if (this.pendingDrawFrameId !== null) {
            cancelAnimationFrame(this.pendingDrawFrameId);
            this.pendingDrawFrameId = null;
        }
        this.cancelPendingViewportChanged();
        if (this.detailTierTimeoutId !== null) {
            clearTimeout(this.detailTierTimeoutId);
            this.detailTierTimeoutId = null;
        }
    }

    private initializeCanvasRenderer(canvas: HTMLCanvasElement): void {
        if (this.offscreenRenderingEnabled()) {
            const detachedContext = this.createDetachedCanvasContext();
            if (!detachedContext) {
                this.initializeMainThreadCanvasRenderer(canvas);
                return;
            }

            const detachedWrapper = new CanvasWrapper(
                detachedContext,
                this.gridSize,
            );
            const offscreenHost = OffscreenCanvasRendererHost.create(
                canvas,
                this.gridSize,
                detachedWrapper.fontState(),
            );
            if (offscreenHost) {
                this.canvasWrapper = detachedWrapper;
                this.rendererHost = offscreenHost;
                return;
            }
        }

        this.initializeMainThreadCanvasRenderer(canvas);
    }

    private initializeMainThreadCanvasRenderer(canvas: HTMLCanvasElement): void {
        const context = canvas.getContext('2d');
        if (!context) {
            return;
        }

        this.canvasWrapper = new CanvasWrapper(context, this.gridSize);
        this.rendererHost = new MainThreadCanvasRendererHost(
            this.canvasWrapper,
        );
    }

    private offscreenRenderingEnabled(): boolean {
        if (this.e2eEnabled()) {
            return false;
        }

        return true;
    }

    private createDetachedCanvasContext(): CanvasRenderingContext2D | null {
        if (typeof OffscreenCanvas !== 'function') {
            return null;
        }

        const context = new OffscreenCanvas(1, 1).getContext('2d');
        return context as CanvasRenderingContext2D | null;
    }

    private resetViewport(): void {
        this.viewportController.resetView(this.renderSnapshot());
    }

    private zoomViewportIn(): void {
        this.pulseSimplifiedDetailTier();
        this.viewportController.zoomIn();
    }

    private zoomViewportOut(): void {
        this.pulseSimplifiedDetailTier();
        this.viewportController.zoomOut();
    }

    private focusViewport(): void {
        const canvas = this.canvasRef()?.nativeElement;
        if (!canvas) {
            return;
        }

        canvas.focus({ preventScroll: true });
        if (!this.isReadonly()) {
            this.emitCanvasEvent({ kind: 'focusChanged', focused: true });
        }
    }

    private centerViewportOnNode(nodeId: number): void {
        const node = this.getNodeById(nodeId);
        if (!node) {
            return;
        }

        this.viewportController.centerOnWorldPoint({
            x: node.x + node.width / 2,
            y: node.y + node.height / 2,
        });
    }

    private revealValidationFocus(focus: CanvasValidationFocus | null): void {
        if (!focus) {
            return;
        }

        this.ensureValidationFocusSectionExpanded(focus);
        const resolvedFocus = this.resolveValidationFocus(
            this.renderSnapshot(),
            focus,
        );
        if (resolvedFocus?.bounds) {
            this.centerViewportOnBounds(resolvedFocus.bounds);
        } else if (focus.nodeId !== null && focus.nodeId !== undefined) {
            this.centerViewportOnNode(focus.nodeId);
        }

        this.focusViewport();
        this.scheduleDraw();
    }

    private centerViewportOnBounds(bounds: CanvasBounds): void {
        this.viewportController.centerOnBounds(bounds);
    }

    private viewportCenterWorld(): Point | null {
        const state = this.viewportController.state();
        if (!state) {
            return null;
        }

        const availableWidth = Math.max(
            1,
            state.viewportSize.width - state.insets.left - state.insets.right,
        );
        const availableHeight = Math.max(
            1,
            state.viewportSize.height - state.insets.top - state.insets.bottom,
        );
        const focus = {
            x: state.insets.left + availableWidth / 2,
            y: state.insets.top + availableHeight / 2,
        };
        return {
            x: (focus.x - state.offset.x) / state.zoom,
            y: (focus.y - state.offset.y) / state.zoom,
        };
    }

    private outputPropertyClientPoint(
        nodeId: number,
        channelName: string | null,
        propertyName: string,
    ): CanvasPropertyClientPoint | null {
        const renderedNode = this.getNodeById(nodeId);
        const anchorIndex = renderedNode
            ? this.outputAnchorIndexByChannel(renderedNode.base, channelName)
            : null;
        const port =
            anchorIndex === null
                ? null
                : (renderedNode?.outputPropertyPort(
                      anchorIndex,
                      propertyName,
                  ) ?? null);
        return port ? this.worldToClientPoint({ x: port.x, y: port.y }) : null;
    }

    private inputPropertyClientPoint(
        nodeId: number,
        propertyName: string,
    ): CanvasPropertyClientPoint | null {
        const renderedNode = this.getNodeById(nodeId);
        const port = renderedNode?.inputPropertyPort(propertyName) ?? null;
        return port ? this.worldToClientPoint({ x: port.x, y: port.y }) : null;
    }

    private viewportContainsClientPoint(
        point: { clientX: number; clientY: number },
        tolerance = 0,
    ): boolean {
        return this.viewportController.containsClientPoint(point, tolerance);
    }

    private hitTestInputPropertyAtClientPoint(
        clientX: number,
        clientY: number,
    ): CanvasInputPropertyHit | null {
        if (!this.viewportContainsClientPoint({ clientX, clientY })) {
            return null;
        }

        const world = this.clientToWorldPoint(clientX, clientY);
        if (!world) {
            return null;
        }

        const target = this.getNodeByCoordinates(world);
        if (!target) {
            return null;
        }

        return createCanvasInputPropertyHit(
            target,
            this.getInputPortAt(target, world),
            clientX,
            clientY,
        );
    }

    protected onFocus(): void {
        if (!this.isReadonly()) {
            this.emitCanvasEvent({ kind: 'focusChanged', focused: true });
        }
    }

    protected onBlur(): void {
        if (!this.isReadonly()) {
            this.emitCanvasEvent({ kind: 'focusChanged', focused: false });
        }
    }

    protected onPointerDown(event: PointerEvent): void {
        const canvas = this.canvasRef()?.nativeElement;
        if (!canvas || !this.canvasWrapper) {
            return;
        }

        this.focusViewport();
        canvas.setPointerCapture(event.pointerId);

        const { canvas: canvasPoint, world } = this.getCoordinates(event);
        const shift = this.canEditGraph() && this.selection().modifiers.shift;
        const node = this.getNodeByCoordinates(world);
        const connection =
            this.canEditGraph() && !node
                ? this.getConnectionByCoordinates(world)
                : undefined;
        const connectionBadge =
            this.canEditGraph() && connection
                ? this.getConnectionBadgeByCoordinates(connection, world)
                : undefined;

        if (this.canMapConnections() && node) {
            const port = this.getPortAt(node, world);
            if (port && this.canStartConnectionFrom(node, port)) {
                const drag: DragConnectionState = {
                    pointerId: event.pointerId,
                    from: node.id,
                    anchor: port.anchorIndex!,
                    x: world.x,
                    y: world.y,
                    side: undefined,
                    portKey: port.key,
                    sourceProperty: port.propertyName,
                };
                this.dragConnection.set(drag);
                if (this.mode() === 'bridge-source') {
                    this.emitBridgeSourceDrag('started', drag, event);
                }
                this.hoveredNodeId.set(node.id);
                this.hoveredNodeIssueNodeId.set(null);
                this.hoveredPortKey.set(port.key);
                this.hoveredSectionKey.set(null);
                this.hoveredConnectionId.set(null);
                this.hoveredConnectionRoute.set(null);
                return;
            }
        }

        const toggleSectionKey =
            node?.sectionToggleHit(world.x, world.y) ??
            node?.sectionHeaderHit(world.x, world.y) ??
            null;
        if (node && toggleSectionKey) {
            this.pendingClick.set({
                pointerId: event.pointerId,
                startCanvas: canvasPoint,
                startWorld: world,
                nodeId: node.id,
                connectionId: null,
                connectionBadgeId: null,
                shift,
                toggleSectionKey,
            });
            return;
        }

        if (this.canEditGraph() && shift && !node && !connection) {
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
            if (this.mode() === 'bridge-source') {
                this.emitBridgeSourceDrag(
                    'moved',
                    {
                        ...dragConnection,
                        x: world.x,
                        y: world.y,
                        side: undefined,
                    },
                    event,
                );
            }
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
                x:
                    panState.initialOffset.x +
                    (canvas.x - panState.startCanvas.x),
                y:
                    panState.initialOffset.y +
                    (canvas.y - panState.startCanvas.y),
            });
            this.notifyViewportChanged();
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
            const transition =
                this.interactionController.pendingClickTransition(
                    pendingClick,
                    canvas,
                    this.canEditGraph(),
                );
            if (transition === 'node-drag') {
                this.startNodeDrag(
                    event.pointerId,
                    pendingClick.nodeId!,
                    world,
                );
                this.pendingClick.set(null);
                return;
            }

            if (transition === 'pan') {
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

        if (this.interactionController.activePointerLifecycle()) {
            return;
        }

        this.interactionController.clearHover();
    }

    protected onWheel(event: WheelEvent): void {
        event.preventDefault();
        this.pulseSimplifiedDetailTier();
        const delta = this.normalizeWheelDelta(event);
        if (this.shouldZoomFromWheel(event)) {
            const dominantDelta =
                Math.abs(delta.y) >= Math.abs(delta.x) ? delta.y : delta.x;
            if (dominantDelta !== 0) {
                this.adjustZoom(
                    Math.sign(-dominantDelta) * 0.05,
                    this.getCoordinates(event).canvas,
                );
            }
            return;
        }

        this.viewportController.panBy(delta);
    }

    private completePointerInteraction(
        event: PointerEvent,
        cancelled: boolean,
    ): void {
        const { canvas, world } = this.getCoordinates(event);
        const dragConnection = this.dragConnection();
        if (dragConnection?.pointerId === event.pointerId) {
            if (this.mode() === 'bridge-source') {
                this.emitBridgeSourceDrag(
                    'ended',
                    dragConnection,
                    event,
                    cancelled,
                );
            } else if (!cancelled) {
                this.finalizeConnectionDrag(dragConnection, world);
            }

            this.dragConnection.set(null);
            this.hoveredPortKey.set(null);
            this.hoveredSectionKey.set(null);
            this.updateHoverState(world);
            return;
        }

        const activeNodeDrag = this.dragNode();
        if (activeNodeDrag?.pointerId === event.pointerId) {
            if (!cancelled) {
                this.commitNodeDrag(activeNodeDrag, world);
            }

            this.transientNodePositions.set(null);
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
                const bounds = this.interactionController.normalizeRect(
                    marqueeState.startWorld,
                    marqueeState.currentWorld,
                );
                this.applyInteractionEffects([
                    { kind: 'selectNodesWithinRect', bounds, additive: true },
                ]);
            }

            this.marqueeState.set(null);
            this.updateHoverState(world);
            return;
        }

        const pendingClick = this.pendingClick();
        if (pendingClick?.pointerId === event.pointerId) {
            this.pendingClick.set(null);
            if (
                cancelled ||
                this.interactionController.movedBeyondClickTolerance(
                    pendingClick.startCanvas,
                    canvas,
                )
            ) {
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
        this.applyInteractionEffects(
            this.interactionController.clickEffects(click, {
                readOnly: this.isReadonly(),
                canEditGraph: this.canEditGraph(),
                nodeIsDataBridge: (nodeId) =>
                    this.getNodeById(nodeId)?.base.isDataBridge ?? false,
            }),
        );
    }

    private applyInteractionEffects(
        effects: readonly CanvasInteractionEffect[],
    ): void {
        for (const effect of effects) {
            switch (effect.kind) {
                case 'toggleNodeSectionExpanded':
                    this.toggleNodeSectionExpanded(
                        effect.nodeId,
                        effect.sectionKey,
                    );
                    break;
                case 'emitReadonlyNodeActivated':
                    this.emitCanvasEvent({
                        kind: 'readonlyNodeActivated',
                        nodeId: effect.nodeId,
                    });
                    break;
                case 'emitNodeSettingsRequested':
                    this.emitCanvasEvent({
                        kind: 'nodeSettingsRequested',
                        nodeId: effect.nodeId,
                    });
                    break;
                case 'selectOnlyNode':
                    this.emitCanvasEvent({
                        kind: 'selectOnlyNode',
                        nodeId: effect.nodeId,
                    });
                    break;
                case 'toggleNodeSelection':
                    this.emitCanvasEvent({
                        kind: 'toggleNodeSelection',
                        nodeId: effect.nodeId,
                    });
                    break;
                case 'selectOnlyConnection':
                    this.emitCanvasEvent({
                        kind: 'selectOnlyConnection',
                        connectionId: effect.connectionId,
                    });
                    break;
                case 'toggleConnectionSelection':
                    this.emitCanvasEvent({
                        kind: 'toggleConnectionSelection',
                        connectionId: effect.connectionId,
                    });
                    break;
                case 'selectNodesWithinRect':
                    this.emitCanvasEvent({
                        kind: 'selectNodesWithinRect',
                        bounds: effect.bounds,
                        additive: effect.additive,
                    });
                    break;
                case 'updateNodePositions':
                    this.emitCanvasEvent({
                        kind: 'updateNodePositions',
                        positions: effect.positions,
                    });
                    break;
                case 'clearSelection':
                    this.emitCanvasEvent({ kind: 'clearSelection' });
                    break;
                case 'emitBridgeSourceDrag':
                case 'createConnection':
                    break;
            }
        }
    }

    private startNodeDrag(
        pointerId: number,
        nodeId: number,
        world: Point,
    ): void {
        if (!this.canEditGraph()) {
            return;
        }

        const dragSource = this.getNodeById(nodeId);
        if (dragSource?.base.isDataBridge) {
            return;
        }

        const selectedNodeIds = this.selection().selectedNodeIds;
        const selectedConnectionIds = this.selection().selectedConnectionIds;
        const useCurrentSelection =
            selectedConnectionIds.length === 0 &&
            selectedNodeIds.includes(nodeId);
        const nodeIds = useCurrentSelection ? selectedNodeIds : [nodeId];

        if (!useCurrentSelection) {
            this.emitCanvasEvent({ kind: 'selectOnlyNode', nodeId });
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

        this.emitCanvasEvent({ kind: 'captureHistory' });
        this.dragNode.set({
            pointerId,
            startWorld: world,
            nodeIds: [...initialPositions.keys()],
            initialPositions,
        });
        this.hoveredNodeId.set(nodeId);
        this.hoveredNodeIssueNodeId.set(null);
        this.hoveredSectionKey.set(null);
    }

    private updateNodeDrag(drag: DragNodeState, world: Point): void {
        this.transientNodePositions.set(this.nodeDragPositions(drag, world));
    }

    private commitNodeDrag(drag: DragNodeState, world: Point): void {
        const positions =
            this.transientNodePositions() ??
            this.nodeDragPositions(drag, world);
        if (
            positions.size === 0 ||
            !this.nodeDragPositionsChanged(drag, positions)
        ) {
            return;
        }

        this.emitCanvasEvent({
            kind: 'updateNodePositions',
            positions: [...positions.entries()].map(
                ([nodeId, coordinates]) => ({
                    nodeId,
                    coordinates,
                }),
            ),
        });
    }

    private nodeDragPositionsChanged(
        drag: DragNodeState,
        positions: ReadonlyMap<number, Point>,
    ): boolean {
        for (const [nodeId, coordinates] of positions) {
            const initial = drag.initialPositions.get(nodeId);
            if (
                !initial ||
                initial.x !== coordinates.x ||
                initial.y !== coordinates.y
            ) {
                return true;
            }
        }

        return false;
    }

    private nodeDragPositions(
        drag: DragNodeState,
        world: Point,
    ): ReadonlyMap<number, Point> {
        const deltaX = world.x - drag.startWorld.x;
        const deltaY = world.y - drag.startWorld.y;
        const positions = new Map<number, Point>();
        for (const nodeId of drag.nodeIds) {
            const initial = drag.initialPositions.get(nodeId);
            if (!initial) {
                continue;
            }

            positions.set(nodeId, {
                x: this.snapCanvasValue(initial.x + deltaX),
                y: this.snapCanvasValue(initial.y + deltaY),
            });
        }

        return positions;
    }

    private updateConnectionDrag(
        drag: DragConnectionState,
        world: Point,
    ): void {
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
            if (
                target.base.nodeKind === 'CollectionIterator' &&
                !this.canDragBindToCollectionIterator(sourceNode, drag)
            ) {
                this.dragConnection.set({
                    ...drag,
                    x: world.x,
                    y: world.y,
                    side: undefined,
                });
                this.hoveredNodeId.set(target.id);
                this.hoveredNodeIssueNodeId.set(null);
                this.hoveredPortKey.set(null);
                this.hoveredSectionKey.set(null);
                this.hoveredConnectionId.set(null);
                this.hoveredConnectionRoute.set(null);
                return;
            }

            const targetPort = this.getInputPortAt(target, world);
            if (targetPort) {
                this.dragConnection.set({
                    ...drag,
                    x: targetPort.x,
                    y: targetPort.y,
                    side: targetPort.side,
                });
                this.hoveredNodeId.set(target.id);
                this.hoveredNodeIssueNodeId.set(null);
                this.hoveredPortKey.set(targetPort.key);
                this.hoveredSectionKey.set(null);
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
        this.hoveredNodeIssueNodeId.set(null);
        this.hoveredPortKey.set(null);
        this.hoveredSectionKey.set(null);
        this.hoveredConnectionId.set(null);
        this.hoveredConnectionRoute.set(null);
    }

    private finalizeConnectionDrag(
        drag: DragConnectionState,
        world: Point,
    ): void {
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
        if (!target || !snap) {
            return;
        }

        if (
            this.mode() === 'bridge-map' &&
            !sourceNode.base.isDataBridge &&
            !target.base.isDataBridge
        ) {
            return;
        }

        if (target.base.isDataBridge) {
            if (
                target.base.dataBridgeKind !== 'output' ||
                sourceNode.base.isDataBridge ||
                !drag.sourceProperty
            ) {
                return;
            }

            const sourceProperties = sourceNode.base.outputPropertiesForAnchor(
                drag.anchor,
            );
            const propertyKind = sourceProperties[drag.sourceProperty];
            if (!propertyKind) {
                return;
            }

            this.emitCanvasEvent({
                kind: 'createConnection',
                request: {
                    from: drag.from,
                    to: target.id,
                    anchor: drag.anchor,
                    side: 'left',
                    position: snap.pos,
                    upsertDataBridgeInputProperty: {
                        nodeId: target.id,
                        propertyName: drag.sourceProperty,
                        propertyKind,
                    },
                    propertyMapping: {
                        sourceProperty: drag.sourceProperty,
                        targetProperty: drag.sourceProperty,
                    },
                },
            });
            return;
        }

        if (!targetPort) {
            return;
        }

        if (sourceNode.base.isDataBridge) {
            if (
                sourceNode.base.dataBridgeKind !== 'input' ||
                target.base.isDataBridge ||
                !drag.sourceProperty ||
                targetPort.kind !== 'input-property' ||
                !targetPort.propertyName
            ) {
                return;
            }

            const sourceProperties = sourceNode.base.outputPropertiesForAnchor(
                drag.anchor,
            );
            const sourcePropertyKind = sourceProperties[drag.sourceProperty];
            const targetPropertyKind =
                target.base.inputProperties[targetPort.propertyName];
            if (
                !sourcePropertyKind ||
                sourcePropertyKind !== targetPropertyKind
            ) {
                return;
            }
        }

        if (
            target.base.nodeKind === 'CollectionIterator' &&
            !this.canDragBindToCollectionIterator(sourceNode, drag)
        ) {
            return;
        }

        if (
            drag.sourceProperty &&
            targetPort.kind === 'input-property' &&
            targetPort.propertyName
        ) {
            this.emitCanvasEvent({
                kind: 'createConnection',
                request: {
                    from: drag.from,
                    to: target.id,
                    anchor: drag.anchor,
                    side: 'left',
                    position: snap.pos,
                    propertyMapping: {
                        sourceProperty: drag.sourceProperty,
                        targetProperty: targetPort.propertyName,
                    },
                },
            });
            return;
        }

        this.emitCanvasEvent({
            kind: 'createConnection',
            request: {
                from: drag.from,
                to: target.id,
                anchor: drag.anchor,
                side: 'left',
                position: snap.pos,
            },
        });
    }

    private updateHoverState(world: Point): void {
        const node = this.getNodeByCoordinates(world);
        const connectionHit =
            !node && this.canEditGraph()
                ? this.getConnectionRouteByCoordinates(world)
                : undefined;

        const candidateHoveredPort =
            !node || !this.canMapConnections()
                ? null
                : this.getPortAt(node, world);
        const hoveredPort =
            candidateHoveredPort &&
            this.isPassiveSummaryPort(candidateHoveredPort)
                ? null
                : candidateHoveredPort;
        const hoveredNodeIssueNodeId =
            !node ||
            !this.canEditGraph() ||
            hoveredPort ||
            !this.nodeIssueSummaries().has(node.id) ||
            !this.pointWithinRect(world, node.settingsRect())
                ? null
                : node.id;
        this.interactionController.setHover({
            nodeId: node?.id ?? null,
            nodeIssueNodeId: hoveredNodeIssueNodeId,
            portKey: hoveredPort?.key ?? null,
            sectionKey:
                !node || this.isReadonly() || hoveredPort
                    ? null
                    : (node.sectionToggleHit(world.x, world.y) ??
                      node.sectionHeaderHit(world.x, world.y) ??
                      null),
            connectionId: connectionHit?.connection.id ?? null,
            connectionRoute: connectionHit
                ? {
                      connectionId: connectionHit.connection.id,
                      routeIndex: connectionHit.routeIndex,
                  }
                : null,
        });
    }

    private isReadonly(): boolean {
        return this.modeCapabilities().readOnly;
    }

    private canEditGraph(): boolean {
        return this.modeCapabilities().graphEditable;
    }

    private canMapConnections(): boolean {
        return this.modeCapabilities().connectionMapping;
    }

    private usesPassiveSummaryPorts(): boolean {
        return this.modeCapabilities().passiveSummaryPorts;
    }

    private shouldDrawConnectionDragPreview(): boolean {
        return this.modeCapabilities().dragConnectionPreview;
    }

    private isPassiveSummaryPort(port: { kind: string }): boolean {
        return isPassiveCanvasSummaryPort(this.mode(), port);
    }

    private canStartConnectionFrom(
        node: RenderedNode,
        port: {
            kind: string;
            anchorIndex: number | null;
            propertyName: string | null;
        },
    ): boolean {
        return canStartCanvasConnectionFrom(this.mode(), node, port);
    }

    private syncCanvasBackingSize(): void {
        const size =
            this.rendererHost?.kind === 'main-thread'
                ? this.viewportController.syncBackingSize(
                      this.canvasWrapper?.ctx as
                          | CanvasRenderingContext2D
                          | null
                          | undefined,
                  )
                : this.viewportController.measureBackingSize();
        if (size) {
            this.canvasBackingSize = size;
            this.rendererHost?.resize(size);
        }
    }

    private adjustZoom(delta: number, focalCanvasPoint?: Point): void {
        this.pulseSimplifiedDetailTier();
        this.viewportController.adjustZoom(delta, focalCanvasPoint);
    }

    private notifyViewportChanged(): void {
        if (this.pendingViewportChangedFrameId !== null) {
            return;
        }

        this.emitCanvasEvent({ kind: 'viewportChanged' });

        const reset = () => {
            this.pendingViewportChangedFrameId = null;
            this.pendingViewportChangedUsesAnimationFrame = false;
        };

        if (typeof requestAnimationFrame === 'function') {
            this.pendingViewportChangedUsesAnimationFrame = true;
            this.pendingViewportChangedFrameId = requestAnimationFrame(reset);
            return;
        }

        this.pendingViewportChangedFrameId = globalThis.setTimeout(
            reset,
            16,
        ) as unknown as number;
    }

    private shouldZoomFromWheel(event: WheelEvent): boolean {
        return event.ctrlKey || this.selection().modifiers.space;
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
            const pageStep = Math.max(
                canvas?.clientHeight ?? 1,
                canvas?.clientWidth ?? 1,
            );
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

    private getCoordinates(event: MouseEvent | PointerEvent): {
        canvas: Point;
        world: Point;
    } {
        return this.viewportController.coordinatesFromEvent(event);
    }

    private hitTestContext() {
        return {
            snapshot: this.renderSnapshot(),
            zoomLevel: this.canvasWrapper?.zoomLevel() ?? 1,
        };
    }

    private getNodeById(id: number): RenderedNode | undefined {
        return this.renderSnapshot().nodeById.get(id);
    }

    private getNodeByCoordinates(
        coordinates: Point,
        ...except: RenderedNode[]
    ): RenderedNode | undefined {
        return this.hitTester.nodeAt(
            this.renderSnapshot(),
            coordinates,
            except,
        );
    }

    private getConnectionByCoordinates(
        coordinates: Point,
    ): RenderedConnection | undefined {
        return this.hitTester.connectionAt(this.hitTestContext(), coordinates);
    }

    private getConnectionRouteByCoordinates(
        coordinates: Point,
    ): ConnectionRouteHit | undefined {
        return this.hitTester.connectionRouteAt(
            this.hitTestContext(),
            coordinates,
        );
    }

    private getConnectionBadgeByCoordinates(
        connection: RenderedConnection,
        coordinates: Point,
    ): RenderedConnection | undefined {
        return this.hitTester.connectionBadgeAt(
            connection,
            coordinates,
            this.canvasWrapper?.zoomLevel() ?? 1,
        );
    }

    private getNodesConnectedTo(node: RenderedNode): RenderedNode[] {
        return this.renderSnapshot().getConnectedNodes(node.id);
    }

    private getBlockedTargetNodeIds(
        snapshot: CanvasRenderSnapshot,
        drag: DragConnectionState,
    ): ReadonlySet<number> {
        return this.hitTester.blockedTargetNodeIds(snapshot, drag);
    }

    private getCollectionIteratorPreviewLabels(
        snapshot: CanvasRenderSnapshot,
        drag: DragConnectionState,
    ): ReadonlyMap<number, string> {
        return this.hitTester.collectionIteratorPreviewLabels(snapshot, drag);
    }

    private canDragBindToCollectionIterator(
        sourceNode: RenderedNode | null,
        drag: DragConnectionState,
    ): boolean {
        return this.hitTester.canDragBindToCollectionIterator(sourceNode, drag);
    }

    private getPortAt(node: RenderedNode, coordinates: Point) {
        return this.hitTester.portAt(
            node,
            coordinates,
            this.canvasWrapper?.zoomLevel() ?? 1,
        );
    }

    private getInputPortAt(node: RenderedNode, coordinates: Point) {
        return this.hitTester.inputPortAt(
            node,
            coordinates,
            this.canvasWrapper?.zoomLevel() ?? 1,
        );
    }

    private clientToWorldPoint(clientX: number, clientY: number): Point | null {
        return this.viewportController.clientToWorldPoint(clientX, clientY);
    }

    private worldToClientPoint(point: Point): CanvasPropertyClientPoint | null {
        return this.viewportController.worldToClientPoint(point);
    }

    private registerE2eCanvas(): void {
        if (!this.e2eEnabled()) {
            return;
        }

        const api = this.ensureE2eApi();
        api.canvases[this.canvasId()] = {
            snapshot: () => this.createE2eSnapshot(),
            requestFrame: () => this.requestE2eFrame(),
            rendererState: () => this.e2eRendererState(),
        };
    }

    private unregisterE2eCanvas(): void {
        if (!this.e2eEnabled() || typeof window === 'undefined') {
            return;
        }

        const api = this.e2eApi();
        if (api) {
            delete api.canvases[this.canvasId()];
        }
    }

    private createE2eSnapshot(): BlueprintCanvasE2eSnapshot | null {
        const canvas = this.canvasRef()?.nativeElement;
        if (!canvas) {
            return null;
        }

        const canvasRect = canvas.getBoundingClientRect();
        return {
            id: this.canvasId(),
            mode: this.mode(),
            zoomPercent: this.zoomPercent(),
            renderer: this.e2eRendererState() ?? {
                kind: 'main-thread',
                submittedFrameId: 0,
                presentedFrameId: 0,
                nodeCount: 0,
                connectionCount: 0,
            },
            canvasRect: {
                left: canvasRect.left,
                top: canvasRect.top,
                width: canvasRect.width,
                height: canvasRect.height,
            },
            viewport: this.viewportController.state(),
            nodes: this.renderSnapshot().nodes.map((node) => {
                const topLeft = this.worldToClientPoint({
                    x: node.x,
                    y: node.y,
                });
                const bottomRight = this.worldToClientPoint({
                    x: node.x + node.width,
                    y: node.y + node.height,
                });
                return {
                    id: node.id,
                    name: node.name,
                    kind: node.base.nodeKind,
                    worldBounds: {
                        left: node.x,
                        top: node.y,
                        right: node.x + node.width,
                        bottom: node.y + node.height,
                    },
                    clientBounds:
                        topLeft && bottomRight
                            ? {
                                  left: topLeft.clientX,
                                  top: topLeft.clientY,
                                  right: bottomRight.clientX,
                                  bottom: bottomRight.clientY,
                              }
                            : null,
                    ports: node.visiblePorts().flatMap((port) => {
                        const point = this.worldToClientPoint({
                            x: port.x,
                            y: port.y,
                        });
                        return point
                            ? [
                                  {
                                      key: port.key,
                                      kind: port.kind,
                                      anchorIndex: port.anchorIndex,
                                      channelName: port.channelName,
                                      propertyName: port.propertyName,
                                      clientX: point.clientX,
                                      clientY: point.clientY,
                                  },
                              ]
                            : [];
                    }),
                    sections: [
                        ...(node.layout.inputSection
                            ? [node.layout.inputSection]
                            : []),
                        ...node.layout.outputSections,
                    ].map((section) => {
                        const headerTopLeft = this.worldToClientPoint({
                            x: node.x + 14,
                            y: section.headerTop,
                        });
                        const headerBottomRight = this.worldToClientPoint({
                            x: node.x + node.width - 14,
                            y: section.headerTop + section.headerHeight,
                        });
                        const toggleTopLeft = this.worldToClientPoint({
                            x: section.toggleRect.x,
                            y: section.toggleRect.y,
                        });
                        const toggleBottomRight = this.worldToClientPoint({
                            x: section.toggleRect.x + section.toggleRect.width,
                            y: section.toggleRect.y + section.toggleRect.height,
                        });
                        return {
                            key: section.key,
                            name: section.name,
                            label: section.label,
                            side: section.side,
                            expanded: section.expanded,
                            headerBounds:
                                headerTopLeft && headerBottomRight
                                    ? {
                                          left: headerTopLeft.clientX,
                                          top: headerTopLeft.clientY,
                                          right: headerBottomRight.clientX,
                                          bottom: headerBottomRight.clientY,
                                      }
                                    : null,
                            toggleBounds:
                                toggleTopLeft && toggleBottomRight
                                    ? {
                                          left: toggleTopLeft.clientX,
                                          top: toggleTopLeft.clientY,
                                          right: toggleBottomRight.clientX,
                                          bottom: toggleBottomRight.clientY,
                                      }
                                    : null,
                        };
                    }),
                };
            }),
        };
    }

    private requestE2eFrame(): BlueprintCanvasE2eRendererState | null {
        this.draw();
        return this.e2eRendererState();
    }

    private e2eRendererState(): BlueprintCanvasE2eRendererState | null {
        const renderer = this.rendererHost;
        if (!renderer) {
            return null;
        }

        return {
            kind: renderer.kind,
            submittedFrameId: renderer.submittedFrameId,
            presentedFrameId: renderer.presentedFrameId,
            nodeCount: renderer.lastSubmittedNodeCount,
            connectionCount: renderer.lastSubmittedConnectionCount,
        };
    }

    private e2eEnabled(): boolean {
        if (typeof window === 'undefined') {
            return false;
        }

        if (new URLSearchParams(window.location.search).get('e2e') === '1') {
            window.sessionStorage.setItem('workflowBuilderE2eCanvas', '1');
            return true;
        }

        return window.sessionStorage.getItem('workflowBuilderE2eCanvas') === '1';
    }

    private ensureE2eApi(): WorkflowBuilderE2eApi {
        const target = window as typeof window & {
            __workflowBuilderE2e?: WorkflowBuilderE2eApi;
        };
        target.__workflowBuilderE2e ??= {
            canvases: {},
            getCanvasSnapshot(id: string): BlueprintCanvasE2eSnapshot | null {
                return this.canvases[id]?.snapshot() ?? null;
            },
        };
        return target.__workflowBuilderE2e;
    }

    private e2eApi(): WorkflowBuilderE2eApi | null {
        return (
            (
                window as typeof window & {
                    __workflowBuilderE2e?: WorkflowBuilderE2eApi;
                }
            ).__workflowBuilderE2e ?? null
        );
    }

    private emitBridgeSourceDrag(
        phase: 'started' | 'moved' | 'ended',
        drag: DragConnectionState,
        event: PointerEvent,
        cancelled = false,
    ): void {
        const payload = this.createBridgeSourceDragEvent(
            drag,
            event.clientX,
            event.clientY,
            cancelled,
        );
        if (payload) {
            this.emitCanvasEvent({
                kind: 'bridgeOutputPropertyDrag',
                phase,
                drag: payload,
            });
        }
    }

    private emitCanvasEvent(event: BlueprintCanvasEvent): void {
        this.canvasEvent.emit(event);
    }

    private createBridgeSourceDragEvent(
        drag: DragConnectionState,
        clientX: number,
        clientY: number,
        cancelled: boolean,
    ): CanvasOutputPropertyDragEvent | null {
        return createCanvasOutputPropertyDragEvent(
            this.getNodeById(drag.from),
            drag,
            clientX,
            clientY,
            cancelled,
        );
    }

    private outputAnchorIndexByChannel(
        node: NodeModel,
        channelName: string | null,
    ): number | null {
        if (!channelName) {
            return null;
        }

        const outputIndex = node.outputChannelNames.indexOf(channelName);
        return outputIndex < 0
            ? null
            : outputIndex + (node.inputChannelName ? 1 : 0);
    }

    private pointWithinRect(
        point: Point,
        rect: { x: number; y: number; width: number; height: number },
    ): boolean {
        return (
            point.x >= rect.x &&
            point.x <= rect.x + rect.width &&
            point.y >= rect.y &&
            point.y <= rect.y + rect.height
        );
    }

    private toggleNodeSectionExpanded(
        nodeId: number,
        sectionKey: string,
    ): void {
        this.canvasStateStore.toggleNodeSectionExpanded(nodeId, sectionKey);
    }

    private hasMovedBeyondThreshold(start: Point, current: Point): boolean {
        return this.interactionController.movedBeyondClickTolerance(
            start,
            current,
        );
    }

    private normalizeRect(
        start: Point,
        end: Point,
    ): { left: number; top: number; right: number; bottom: number } {
        return this.interactionController.normalizeRect(start, end);
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
        const rendererHost = this.rendererHost;
        const canvas = this.canvasRef()?.nativeElement;
        if (!wrapper || !rendererHost || !canvas) {
            return;
        }

        const offset = wrapper.viewportOffset();
        const zoom = wrapper.zoomLevel();
        const renderedGraph = this.renderSnapshot();
        const frame = this.renderFrameFactory.createFramePlan({
            snapshot: renderedGraph,
            capabilities: this.modeCapabilities(),
            connectionPresentation: this.connectionPresentation(),
            interaction: this.interactionController.state(),
            readonlySelectedNodeId: this.readonlySelectedNodeId(),
            selectedNodeIds: this.selectedNodeIds(),
            selectedConnectionIds: this.selectedConnectionIds(),
            validationFocus: this.resolvedValidationFocus(),
            nodeIssueSummaries: this.nodeIssueSummaries(),
        });
        const cullPlan = this.renderCullPlan(
            renderedGraph,
            frame,
            canvas,
            offset,
            zoom,
        );

        rendererHost.drawFrame({
            canvasWidth: this.canvasBackingSize.width,
            canvasHeight: this.canvasBackingSize.height,
            canvasClientWidth: canvas.clientWidth,
            canvasClientHeight: canvas.clientHeight,
            offset,
            zoom,
            backgroundEffect: this.backgroundEffect(),
            backdropHoverCanvasPoint: this.backdropHoverCanvasPoint(),
            snapshot: renderedGraph,
            frame,
            cullPlan,
            dragConnection: this.dragConnection(),
            hoveredNodeId: this.hoveredNodeId(),
            hoveredPortKey: this.hoveredPortKey(),
        });
    }

    private renderCullPlan(
        snapshot: CanvasRenderSnapshot,
        frame: CanvasRenderFramePlan,
        canvas: HTMLCanvasElement,
        offset: Point,
        zoom: number,
    ): CanvasRenderCullPlan {
        if (!this.modeCapabilities().viewportCulling) {
            return {
                nodes: snapshot.nodes,
                connections: snapshot.connections,
            };
        }

        return createCanvasRenderCullPlan({
            snapshot,
            visibleWorldBounds: getVisibleWorldBounds(
                canvas.clientWidth,
                canvas.clientHeight,
                offset,
                zoom,
            ),
            worldPadding: Math.max(180, 260 / Math.max(zoom, 0.0001)),
            forcedNodeIds: this.forcedCullNodeIds(frame),
            forcedConnectionIds: this.forcedCullConnectionIds(frame),
        });
    }

    private forcedCullNodeIds(
        frame: CanvasRenderFramePlan,
    ): ReadonlySet<number> {
        const nodeIds = new Set(frame.nodeLayer.selectedNodeIds);
        const hoveredNodeId = this.hoveredNodeId();
        const validationNodeId = this.resolvedValidationFocus()?.nodeId ?? null;
        const dragNode = this.dragNode();
        const dragConnection = this.dragConnection();

        if (hoveredNodeId !== null) {
            nodeIds.add(hoveredNodeId);
        }
        if (validationNodeId !== null) {
            nodeIds.add(validationNodeId);
        }
        if (frame.failureTooltip) {
            nodeIds.add(frame.failureTooltip.node.id);
        }
        this.addObstacleNodeId(nodeIds, frame.connectionPreview?.startNode);
        this.addObstacleNodeId(nodeIds, frame.connectionPreview?.destNode);
        if (dragNode) {
            for (const nodeId of dragNode.nodeIds) {
                nodeIds.add(nodeId);
            }
        }
        if (dragConnection) {
            nodeIds.add(dragConnection.from);
        }

        return nodeIds;
    }

    private addObstacleNodeId(nodeIds: Set<number>, obstacle: unknown): void {
        const id = (obstacle as { readonly id?: unknown } | null | undefined)
            ?.id;
        if (typeof id === 'number') {
            nodeIds.add(id);
        }
    }

    private forcedCullConnectionIds(
        frame: CanvasRenderFramePlan,
    ): ReadonlySet<string> {
        const connectionIds = new Set(
            frame.connectionLayer.selectedConnectionIds,
        );
        const hoveredConnectionId =
            frame.connectionLayer.options.hoveredConnectionId ?? null;
        const validationConnectionId =
            frame.connectionLayer.options.validationConnectionFocus
                ?.connectionId ?? null;

        if (hoveredConnectionId) {
            connectionIds.add(hoveredConnectionId);
        }
        if (validationConnectionId) {
            connectionIds.add(validationConnectionId);
        }
        for (const connectionId of frame.connectionLayer.options
            .channelFocusedConnectionIds ?? []) {
            connectionIds.add(connectionId);
        }

        return connectionIds;
    }

    private cancelPendingViewportChanged(): void {
        if (this.pendingViewportChangedFrameId === null) {
            return;
        }

        if (this.pendingViewportChangedUsesAnimationFrame) {
            cancelAnimationFrame(this.pendingViewportChangedFrameId);
        } else {
            clearTimeout(this.pendingViewportChangedFrameId);
        }

        this.pendingViewportChangedFrameId = null;
        this.pendingViewportChangedUsesAnimationFrame = false;
    }

    private buildNodeIssueSummaries(
        markers: readonly CanvasValidationIssueMarker[],
    ): ReadonlyMap<number, CanvasNodeIssueSummary> {
        return this.validationFocusResolver.buildNodeIssueSummaries(markers);
    }

    private snapshotPathfinderOptions() {
        return {
            gridSize: this.gridSize,
            nodeMargin: NodeModel.buffer,
            obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
        };
    }

    private snapCanvasValue(value: number): number {
        return Math.round(value / this.gridSize) * this.gridSize;
    }

    private ensureValidationFocusSectionExpanded(
        focus: CanvasValidationFocus,
    ): void {
        if (
            focus.nodeId === null ||
            focus.nodeId === undefined ||
            !focus.propertyName ||
            !focus.channelName
        ) {
            return;
        }

        const node = this.renderSnapshot().nodeById.get(focus.nodeId);
        const sectionKey = node
            ? this.validationFocusResolver.sectionKeyForValidationFocus(
                  node,
                  focus,
              )
            : null;
        if (!sectionKey) {
            return;
        }

        this.canvasStateStore.ensureNodeSectionExpanded(
            focus.nodeId,
            sectionKey,
        );
    }

    private resolveValidationFocus(
        snapshot: CanvasRenderSnapshot,
        focus: CanvasValidationFocus | null,
    ): ResolvedValidationFocus | null {
        return this.validationFocusResolver.resolveValidationFocus({
            snapshot,
            focus,
        });
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
