import { signal } from '@angular/core';
import { Point } from './node-model';

export type CanvasPendingPointerTransition = 'none' | 'node-drag' | 'pan';

export interface CanvasPendingClickState {
  readonly pointerId: number;
  readonly startCanvas: Point;
  readonly startWorld: Point;
  readonly nodeId: number | null;
  readonly connectionId: string | null;
  readonly connectionBadgeId: string | null;
  readonly shift: boolean;
  readonly toggleSectionKey: string | null;
}

export interface CanvasDragNodeState {
  readonly pointerId: number;
  readonly startWorld: Point;
  readonly nodeIds: readonly number[];
  readonly initialPositions: ReadonlyMap<number, Point>;
}

export interface CanvasDragConnectionState {
  readonly pointerId: number;
  readonly from: number;
  readonly anchor: number;
  readonly x: number;
  readonly y: number;
  readonly side?: 'left' | 'right' | 'top' | 'bottom';
  readonly portKey?: string | null;
  readonly sourceProperty?: string | null;
}

export interface CanvasPanState {
  readonly pointerId: number;
  readonly startCanvas: Point;
  readonly initialOffset: Point;
}

export interface CanvasMarqueeState {
  readonly pointerId: number;
  readonly startWorld: Point;
  readonly currentWorld: Point;
}

export interface CanvasHoverState {
  readonly nodeId: number | null;
  readonly nodeIssueNodeId: number | null;
  readonly portKey: string | null;
  readonly sectionKey: string | null;
  readonly connectionId: string | null;
  readonly connectionRoute: {
    readonly connectionId: string;
    readonly routeIndex: number;
  } | null;
}

export interface CanvasInteractionState extends CanvasHoverState {
  readonly backdropHoverCanvasPoint: Point | null;
  readonly pendingClick: CanvasPendingClickState | null;
  readonly dragNode: CanvasDragNodeState | null;
  readonly dragConnection: CanvasDragConnectionState | null;
  readonly pan: CanvasPanState | null;
  readonly marquee: CanvasMarqueeState | null;
}

export type CanvasInteractionEffect =
  | { readonly kind: 'toggleNodeSectionExpanded'; readonly nodeId: number; readonly sectionKey: string }
  | { readonly kind: 'emitReadonlyNodeActivated'; readonly nodeId: number }
  | { readonly kind: 'emitNodeSettingsRequested'; readonly nodeId: number }
  | { readonly kind: 'selectOnlyNode'; readonly nodeId: number }
  | { readonly kind: 'toggleNodeSelection'; readonly nodeId: number }
  | { readonly kind: 'selectOnlyConnection'; readonly connectionId: string }
  | { readonly kind: 'toggleConnectionSelection'; readonly connectionId: string }
  | { readonly kind: 'selectNodesWithinRect'; readonly bounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number }; readonly additive: boolean }
  | { readonly kind: 'updateNodePositions'; readonly positions: readonly { readonly nodeId: number; readonly coordinates: Point }[] }
  | { readonly kind: 'emitBridgeSourceDrag'; readonly phase: 'started' | 'moved' | 'ended'; readonly drag: CanvasDragConnectionState; readonly cancelled: boolean }
  | { readonly kind: 'createConnection'; readonly from: number; readonly to: number; readonly anchor: number; readonly side: 'left'; readonly position: number }
  | { readonly kind: 'clearSelection' };

export interface CanvasClickEffectContext {
  readonly readOnly: boolean;
  readonly canEditGraph: boolean;
  readonly nodeIsDataBridge: (nodeId: number) => boolean;
}

export class CanvasInteractionController {
  readonly hoveredNodeId = signal<number | null>(null);
  readonly hoveredNodeIssueNodeId = signal<number | null>(null);
  readonly hoveredPortKey = signal<string | null>(null);
  readonly hoveredSectionKey = signal<string | null>(null);
  readonly hoveredConnectionId = signal<string | null>(null);
  readonly hoveredConnectionRoute = signal<CanvasHoverState['connectionRoute']>(null);
  readonly backdropHoverCanvasPoint = signal<Point | null>(null);
  readonly pendingClick = signal<CanvasPendingClickState | null>(null);
  readonly dragNode = signal<CanvasDragNodeState | null>(null);
  readonly dragConnection = signal<CanvasDragConnectionState | null>(null);
  readonly panState = signal<CanvasPanState | null>(null);
  readonly marqueeState = signal<CanvasMarqueeState | null>(null);

  constructor(private readonly clickTolerance: number) {}

  state(): CanvasInteractionState {
    return {
      nodeId: this.hoveredNodeId(),
      nodeIssueNodeId: this.hoveredNodeIssueNodeId(),
      portKey: this.hoveredPortKey(),
      sectionKey: this.hoveredSectionKey(),
      connectionId: this.hoveredConnectionId(),
      connectionRoute: this.hoveredConnectionRoute(),
      backdropHoverCanvasPoint: this.backdropHoverCanvasPoint(),
      pendingClick: this.pendingClick(),
      dragNode: this.dragNode(),
      dragConnection: this.dragConnection(),
      pan: this.panState(),
      marquee: this.marqueeState(),
    };
  }

  movedBeyondClickTolerance(start: Point, current: Point): boolean {
    return Math.abs(current.x - start.x) > this.clickTolerance ||
      Math.abs(current.y - start.y) > this.clickTolerance;
  }

  pendingClickTransition(
    pendingClick: CanvasPendingClickState,
    currentCanvasPoint: Point,
    canEditGraph: boolean,
  ): CanvasPendingPointerTransition {
    if (pendingClick.toggleSectionKey !== null ||
      !this.movedBeyondClickTolerance(pendingClick.startCanvas, currentCanvasPoint)) {
      return 'none';
    }

    if (pendingClick.nodeId !== null && !pendingClick.shift && canEditGraph) {
      return 'node-drag';
    }

    if (pendingClick.nodeId === null && pendingClick.connectionId === null) {
      return 'pan';
    }

    return 'none';
  }

  clickEffects(click: CanvasPendingClickState, context: CanvasClickEffectContext): readonly CanvasInteractionEffect[] {
    if (click.toggleSectionKey && click.nodeId !== null) {
      return [{ kind: 'toggleNodeSectionExpanded', nodeId: click.nodeId, sectionKey: click.toggleSectionKey }];
    }

    if (context.readOnly) {
      return click.nodeId !== null
        ? [{ kind: 'emitReadonlyNodeActivated', nodeId: click.nodeId }]
        : [];
    }

    if (!context.canEditGraph) {
      return [{ kind: 'clearSelection' }];
    }

    if (click.nodeId !== null) {
      if (context.nodeIsDataBridge(click.nodeId)) {
        return [];
      }

      return click.shift
        ? [{ kind: 'toggleNodeSelection', nodeId: click.nodeId }]
        : [
          { kind: 'selectOnlyNode', nodeId: click.nodeId },
          { kind: 'emitNodeSettingsRequested', nodeId: click.nodeId },
        ];
    }

    if (click.connectionId !== null) {
      return click.shift
        ? [{ kind: 'toggleConnectionSelection', connectionId: click.connectionId }]
        : [{ kind: 'selectOnlyConnection', connectionId: click.connectionId }];
    }

    return [{ kind: 'clearSelection' }];
  }

  clearHover(): void {
    this.hoveredNodeId.set(null);
    this.hoveredNodeIssueNodeId.set(null);
    this.hoveredPortKey.set(null);
    this.hoveredSectionKey.set(null);
    this.hoveredConnectionId.set(null);
    this.hoveredConnectionRoute.set(null);
  }

  setHover(hover: CanvasHoverState): void {
    this.hoveredNodeId.set(hover.nodeId);
    this.hoveredNodeIssueNodeId.set(hover.nodeIssueNodeId);
    this.hoveredPortKey.set(hover.portKey);
    this.hoveredSectionKey.set(hover.sectionKey);
    this.hoveredConnectionId.set(hover.connectionId);
    this.hoveredConnectionRoute.set(hover.connectionRoute);
  }

  clearPointerLifecycle(): void {
    this.pendingClick.set(null);
    this.dragNode.set(null);
    this.dragConnection.set(null);
    this.panState.set(null);
    this.marqueeState.set(null);
  }

  activePointerLifecycle(): boolean {
    return !!(this.dragConnection() || this.dragNode() || this.panState() || this.marqueeState());
  }

  normalizeRect(start: Point, end: Point): { left: number; top: number; right: number; bottom: number } {
    return {
      left: Math.min(start.x, end.x),
      top: Math.min(start.y, end.y),
      right: Math.max(start.x, end.x),
      bottom: Math.max(start.y, end.y),
    };
  }
}
