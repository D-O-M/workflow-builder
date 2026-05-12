import type { Point, Side } from './node-model';
import type { RouteProblemSegment, RouteStatus, Segment } from './pathfinder';
import type { RenderedConnection } from './canvas-render-connection-model';
import type { RenderedNode } from './canvas-render-node-model';

export type CanvasDetailTier = 'full' | 'simplified';
export type CanvasConnectionPresentation = 'normal' | 'ambient';
export type CanvasValidationSeverity = 'error' | 'warning';
export type CanvasConnectionSemanticStatus = 'inactive' | 'completed' | 'failed';

export interface CanvasPortConnectivityIndex {
  readonly summaryPortKeys: ReadonlySet<string>;
  readonly propertyPortKeys: ReadonlySet<string>;
}

export interface CanvasBounds {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

export interface CanvasRenderSnapshot {
  readonly detailTier: CanvasDetailTier;
  readonly nodes: RenderedNode[];
  readonly connections: RenderedConnection[];
  readonly routeValidationErrors: readonly CanvasRouteValidationError[];
  readonly nodeById: ReadonlyMap<number, RenderedNode>;
  readonly connectionById: ReadonlyMap<string, RenderedConnection>;
  readonly portConnectivity: CanvasPortConnectivityIndex;
  getConnectedNodeIds(nodeId: number): ReadonlySet<number>;
  getConnectedNodes(nodeId: number): RenderedNode[];
  queryNodesAtPoint(x: number, y: number, exceptIds?: ReadonlySet<number>): RenderedNode[];
  queryConnectionsAtPoint(x: number, y: number): RenderedConnection[];
}

export interface CanvasRouteValidationError {
  readonly connectionId: string;
  readonly routeIndex: number;
  readonly message: string;
}

export const CONNECTION_STROKE_WIDTH = 3.2;
export const LANE_CENTER_SPACING = 7.2;

const CONNECTION_FOCUSED_STROKE_WIDTH = 8.2;
const CONNECTION_HALO_WIDTH = 18;

export interface CanvasConnectionLane {
  readonly offset: number;
  readonly index: number;
  readonly groupSize: number;
}

export interface CanvasConnectionShade {
  readonly index: number;
  readonly total: number;
  readonly sourceIndex: number;
  readonly sourceTotal: number;
  readonly variantIndex: number;
  readonly variantTotal: number;
  readonly semanticStatus?: CanvasConnectionSemanticStatus;
}

export interface RenderedConnectionRoute {
  readonly segments: readonly Segment[];
  readonly visualSegments: readonly Segment[];
  readonly status: RouteStatus;
  readonly problemSegments: readonly RouteProblemSegment[];
  readonly visualProblemSegments: readonly RouteProblemSegment[];
  readonly validationMessage?: string;
  readonly tip: Point;
  readonly startSide: Side;
  readonly side: Side;
  readonly bounds: CanvasBounds | null;
  readonly canonicalBounds: CanvasBounds | null;
  readonly lane: CanvasConnectionLane;
  readonly shade: CanvasConnectionShade;
  readonly startPortKey: string;
  readonly endPortKey: string;
  readonly sourceProperty: string | null;
  readonly targetProperty: string | null;
}


export { CONNECTION_FOCUSED_STROKE_WIDTH, CONNECTION_HALO_WIDTH };
