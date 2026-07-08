import { CanvasBackgroundEffect } from './canvas-backdrop';
import { CanvasDragConnectionState } from './canvas-interaction-controller';
import {
    CanvasNodeIssueSummary,
    CanvasPropertyIssueSummary,
} from './canvas-node-render-types';
import { CanvasRenderCullPlan } from './canvas-render-culling';
import {
    CanvasConnectionLane,
    CanvasConnectionPresentation,
    CanvasConnectionShade,
    CanvasDetailTier,
    CanvasPortConnectivityIndex,
    CanvasRenderSnapshot,
    CanvasValidationSeverity,
    RenderedConnectionRoute,
} from './canvas-render-model';
import { CanvasRenderFramePlan } from './canvas-render-frame-factory';
import { CanvasRenderFrameRenderRequest } from './canvas-render-frame-renderer';
import { CanvasNodeValidationFocus } from './canvas-node-renderer';
import {
    NodeChannelSectionLayout,
    NodeKind,
    NodeLayout,
    NodePortDefinition,
    NodeRenderStatus,
    Point,
    Side,
} from './node-model';
import {
    PathfinderObstacle,
    RouteProblemSegment,
    RouteStatus,
    Segment,
} from './pathfinder';
import { RenderedConnection } from './canvas-render-connection-model';
import { RenderedNode } from './canvas-render-node-model';

export type CanvasWorkerSerializableFrameRequest = Omit<CanvasRenderFrameRenderRequest, 'wrapper'>;

export type CanvasWorkerMessage =
    | CanvasWorkerInitMessage
    | CanvasWorkerResizeMessage
    | CanvasWorkerFontMessage
    | CanvasWorkerFrameMessage
    | CanvasWorkerDestroyMessage;

export type CanvasWorkerHostMessage = CanvasWorkerFramePresentedMessage;

export interface CanvasWorkerFramePresentedMessage {
    readonly kind: 'frame-presented';
    readonly frameId: number;
}

export interface CanvasWorkerInitMessage {
    readonly kind: 'init';
    readonly canvas: OffscreenCanvas;
    readonly gridSize: number;
    readonly font: CanvasWorkerFontState;
}

export interface CanvasWorkerResizeMessage {
    readonly kind: 'resize';
    readonly width: number;
    readonly height: number;
    readonly devicePixelRatio: number;
}

export interface CanvasWorkerFontMessage {
    readonly kind: 'font';
    readonly font: CanvasWorkerFontState;
}

export interface CanvasWorkerFrameMessage {
    readonly kind: 'frame';
    readonly frameId: number;
    readonly frame: CanvasWorkerFramePayload;
}

export interface CanvasWorkerDestroyMessage {
    readonly kind: 'destroy';
}

export interface CanvasWorkerFontState {
    readonly fontFamily: string;
    readonly fontReady: boolean;
    readonly materialIconReady: boolean;
}

export interface CanvasWorkerFramePayload {
    readonly canvasWidth: number;
    readonly canvasHeight: number;
    readonly canvasClientWidth: number;
    readonly canvasClientHeight: number;
    readonly offset: Point;
    readonly zoom: number;
    readonly backgroundEffect: CanvasBackgroundEffect;
    readonly backdropHoverCanvasPoint: Point | null;
    readonly snapshot: CanvasWorkerSnapshotDto;
    readonly frame: CanvasWorkerFramePlanDto;
    readonly cullPlan: CanvasWorkerCullPlanDto;
    readonly dragConnection: CanvasDragConnectionState | null;
    readonly hoveredNodeId: number | null;
    readonly hoveredPortKey: string | null;
}

export interface CanvasWorkerSnapshotDto {
    readonly portConnectivity: {
        readonly summaryPortKeys: readonly string[];
        readonly propertyPortKeys: readonly string[];
    };
}

export interface CanvasWorkerCullPlanDto {
    readonly nodes: readonly CanvasWorkerNodeDto[];
    readonly connections: readonly CanvasWorkerConnectionDto[];
}

export interface CanvasWorkerFramePlanDto {
    readonly connectionLayer: {
        readonly selectedConnectionIds: readonly string[];
        readonly options: CanvasWorkerConnectionRenderOptionsDto;
    };
    readonly nodeLayer: {
        readonly selectedNodeIds: readonly number[];
        readonly options: CanvasWorkerNodeRenderOptionsDto;
    };
    readonly connectionPreview: CanvasWorkerConnectionPreviewDto | null;
    readonly marqueeBounds: CanvasRenderFramePlan['marqueeBounds'];
    readonly failureTooltip: {
        readonly node: CanvasWorkerNodeDto;
        readonly message: string;
    } | null;
    readonly hoverTooltip: CanvasRenderFramePlan['hoverTooltip'];
}

export interface CanvasWorkerConnectionRenderOptionsDto {
    readonly readOnly?: boolean;
    readonly detailTier?: CanvasDetailTier;
    readonly connectionPresentation?: CanvasConnectionPresentation;
    readonly hoveredConnectionId?: string | null;
    readonly hoveredConnectionRouteIndex?: number | null;
    readonly channelFocusedConnectionIds?: readonly string[];
    readonly validationConnectionFocus?: {
        readonly connectionId: string;
        readonly routeIndex?: number | null;
        readonly severity: CanvasValidationSeverity;
    } | null;
}

export interface CanvasWorkerNodeRenderOptionsDto {
    readonly readOnly?: boolean;
    readonly detailTier?: CanvasDetailTier;
    readonly blockedTargetNodeIds?: readonly number[];
    readonly focusedPortKeys?: readonly string[];
    readonly collectionIteratorPreviewLabels?: readonly [number, string][];
    readonly nodeIssueSummaries?: readonly [
        number,
        CanvasWorkerNodeIssueSummaryDto,
    ][];
    readonly passiveSummaryPorts?: boolean;
    readonly validationFocus?: CanvasWorkerNodeValidationFocusDto | null;
}

export interface CanvasWorkerNodeValidationFocusDto {
    readonly severity: CanvasValidationSeverity;
    readonly nodeId: number | null;
    readonly sectionKey?: string | null;
    readonly portKeys?: readonly string[];
}

export interface CanvasWorkerNodeIssueSummaryDto {
    readonly severity: CanvasValidationSeverity;
    readonly errorCount: number;
    readonly warningCount: number;
    readonly tooltip: string;
    readonly propertyIssues: readonly [string, CanvasPropertyIssueSummary][];
}

export interface CanvasWorkerConnectionPreviewDto {
    readonly sx: number;
    readonly sy: number;
    readonly ex: number;
    readonly ey: number;
    readonly obstacles: readonly CanvasWorkerNodeDto[];
    readonly side?: Side;
    readonly destNode?: CanvasWorkerNodeDto;
    readonly startSide?: Side;
    readonly startNode?: CanvasWorkerNodeDto;
}

export interface CanvasWorkerNodeDto {
    readonly id: number;
    readonly x: number;
    readonly y: number;
    readonly width: number;
    readonly height: number;
    readonly layout: NodeLayout;
    readonly renderStatus: NodeRenderStatus | null;
    readonly failureMessage: string | null;
    readonly hasUpgradeWarning: boolean;
    readonly resolvedVersion: string | null;
    readonly name: string;
    readonly isDataBridge: boolean;
    readonly isCollectionIteratorOperator: boolean;
    readonly hasDesignTimeOverrides: boolean;
    readonly designTimeOverrideCount: number;
    readonly base: {
        readonly nodeKind: NodeKind;
        readonly collectionIteratorResolved: boolean;
        readonly collectionIteratorEquation: string;
    };
}

export interface CanvasWorkerConnectionDto {
    readonly id: string;
    readonly routes: readonly CanvasWorkerRouteDto[];
}

export interface CanvasWorkerRouteDto {
    readonly segments: readonly Segment[];
    readonly visualSegments: readonly Segment[];
    readonly status: RouteStatus;
    readonly problemSegments: readonly CanvasWorkerProblemSegmentDto[];
    readonly visualProblemSegments: readonly CanvasWorkerProblemSegmentDto[];
    readonly validationMessage?: string;
    readonly tip: Point;
    readonly startSide: Side;
    readonly side: Side;
    readonly bounds: RenderedConnectionRoute['bounds'];
    readonly canonicalBounds: RenderedConnectionRoute['canonicalBounds'];
    readonly lane: CanvasConnectionLane;
    readonly shade: CanvasConnectionShade;
    readonly startPortKey: string;
    readonly endPortKey: string;
    readonly sourceProperty: string | null;
    readonly targetProperty: string | null;
}

export interface CanvasWorkerProblemSegmentDto extends Segment {}

export function serializeCanvasWorkerFrame(
    request: CanvasWorkerSerializableFrameRequest,
): CanvasWorkerFramePayload {
    return {
        canvasWidth: request.canvasWidth,
        canvasHeight: request.canvasHeight,
        canvasClientWidth: request.canvasClientWidth,
        canvasClientHeight: request.canvasClientHeight,
        offset: request.offset,
        zoom: request.zoom,
        backgroundEffect: request.backgroundEffect,
        backdropHoverCanvasPoint: request.backdropHoverCanvasPoint,
        snapshot: {
            portConnectivity: {
                summaryPortKeys: [
                    ...request.snapshot.portConnectivity.summaryPortKeys,
                ],
                propertyPortKeys: [
                    ...request.snapshot.portConnectivity.propertyPortKeys,
                ],
            },
        },
        frame: serializeFramePlan(request.frame),
        cullPlan: {
            nodes: request.cullPlan.nodes.map(serializeNode),
            connections: request.cullPlan.connections.map(serializeConnection),
        },
        dragConnection: request.dragConnection,
        hoveredNodeId: request.hoveredNodeId,
        hoveredPortKey: request.hoveredPortKey,
    };
}

export function reviveCanvasWorkerFrame(
    payload: CanvasWorkerFramePayload,
    wrapper: CanvasRenderFrameRenderRequest['wrapper'],
): CanvasRenderFrameRenderRequest {
    const snapshot = {
        portConnectivity: {
            summaryPortKeys: new Set(
                payload.snapshot.portConnectivity.summaryPortKeys,
            ),
            propertyPortKeys: new Set(
                payload.snapshot.portConnectivity.propertyPortKeys,
            ),
        } satisfies CanvasPortConnectivityIndex,
    } as unknown as CanvasRenderSnapshot;
    const nodeById = new Map<number, RenderedNode>();
    const reviveNodeById = (node: CanvasWorkerNodeDto): RenderedNode => {
        const existing = nodeById.get(node.id);
        if (existing) {
            return existing;
        }

        const revived = reviveNode(node);
        nodeById.set(node.id, revived);
        return revived;
    };

    return {
        wrapper,
        canvasWidth: payload.canvasWidth,
        canvasHeight: payload.canvasHeight,
        canvasClientWidth: payload.canvasClientWidth,
        canvasClientHeight: payload.canvasClientHeight,
        offset: payload.offset,
        zoom: payload.zoom,
        backgroundEffect: payload.backgroundEffect,
        backdropHoverCanvasPoint: payload.backdropHoverCanvasPoint,
        snapshot,
        frame: reviveFramePlan(payload.frame, reviveNodeById),
        cullPlan: {
            nodes: payload.cullPlan.nodes.map(reviveNodeById),
            connections: payload.cullPlan.connections.map(reviveConnection),
        },
        dragConnection: payload.dragConnection,
        hoveredNodeId: payload.hoveredNodeId,
        hoveredPortKey: payload.hoveredPortKey,
    };
}

function serializeFramePlan(
    frame: CanvasRenderFramePlan,
): CanvasWorkerFramePlanDto {
    return {
        connectionLayer: {
            selectedConnectionIds: [
                ...frame.connectionLayer.selectedConnectionIds,
            ],
            options: {
                ...frame.connectionLayer.options,
                channelFocusedConnectionIds: [
                    ...(frame.connectionLayer.options
                        .channelFocusedConnectionIds ?? []),
                ],
            },
        },
        nodeLayer: {
            selectedNodeIds: [...frame.nodeLayer.selectedNodeIds],
            options: serializeNodeOptions(frame.nodeLayer.options),
        },
        connectionPreview: frame.connectionPreview
            ? {
                  ...frame.connectionPreview,
                  obstacles: frame.connectionPreview.obstacles.map((node) =>
                      serializeNode(node as RenderedNode),
                  ),
                  destNode: frame.connectionPreview.destNode
                      ? serializeNode(
                            frame.connectionPreview.destNode as RenderedNode,
                        )
                      : undefined,
                  startNode: frame.connectionPreview.startNode
                      ? serializeNode(
                            frame.connectionPreview.startNode as RenderedNode,
                        )
                      : undefined,
              }
            : null,
        marqueeBounds: frame.marqueeBounds,
        failureTooltip: frame.failureTooltip
            ? {
                  node: serializeNode(frame.failureTooltip.node),
                  message: frame.failureTooltip.message,
              }
            : null,
        hoverTooltip: frame.hoverTooltip,
    };
}

function serializeNodeOptions(
    options: CanvasRenderFramePlan['nodeLayer']['options'],
): CanvasWorkerNodeRenderOptionsDto {
    return {
        ...options,
        blockedTargetNodeIds: [...(options.blockedTargetNodeIds ?? [])],
        focusedPortKeys: [...(options.focusedPortKeys ?? [])],
        collectionIteratorPreviewLabels: [
            ...(
                options.collectionIteratorPreviewLabels ??
                new Map<number, string>()
            ).entries(),
        ],
        nodeIssueSummaries: [
            ...(
                options.nodeIssueSummaries ??
                new Map<number, CanvasNodeIssueSummary>()
            ).entries(),
        ].map(
            ([nodeId, summary]) =>
                [nodeId, serializeNodeIssueSummary(summary)] as const,
        ),
        validationFocus: options.validationFocus
            ? {
                  ...options.validationFocus,
                  portKeys: [...(options.validationFocus.portKeys ?? [])],
              }
            : null,
    };
}

function serializeNodeIssueSummary(
    summary: CanvasNodeIssueSummary,
): CanvasWorkerNodeIssueSummaryDto {
    return {
        severity: summary.severity,
        errorCount: summary.errorCount,
        warningCount: summary.warningCount,
        tooltip: summary.tooltip,
        propertyIssues: [...summary.propertyIssues.entries()],
    };
}

function serializeNode(node: RenderedNode): CanvasWorkerNodeDto {
    return {
        id: node.id,
        x: node.x,
        y: node.y,
        width: node.width,
        height: node.height,
        layout: node.layout,
        renderStatus: node.renderStatus,
        failureMessage: node.failureMessage,
        hasUpgradeWarning: node.hasUpgradeWarning,
        resolvedVersion: node.resolvedVersion,
        name: node.name,
        isDataBridge: node.isDataBridge,
        isCollectionIteratorOperator: node.isCollectionIteratorOperator,
        hasDesignTimeOverrides: node.hasDesignTimeOverrides,
        designTimeOverrideCount: node.designTimeOverrideCount,
        base: {
            nodeKind: node.base.nodeKind,
            collectionIteratorResolved: node.base.collectionIteratorResolved,
            collectionIteratorEquation: node.base.collectionIteratorEquation,
        },
    };
}

function serializeConnection(
    connection: RenderedConnection,
): CanvasWorkerConnectionDto {
    return {
        id: connection.id,
        routes: connection.routes.map((route) => ({
            ...route,
            problemSegments: route.problemSegments.map(stripProblemSegment),
            visualProblemSegments:
                route.visualProblemSegments.map(stripProblemSegment),
        })),
    };
}

function stripProblemSegment(
    segment: RouteProblemSegment,
): CanvasWorkerProblemSegmentDto {
    return {
        x1: segment.x1,
        y1: segment.y1,
        x2: segment.x2,
        y2: segment.y2,
    };
}

function reviveFramePlan(
    frame: CanvasWorkerFramePlanDto,
    reviveNodeById: (node: CanvasWorkerNodeDto) => RenderedNode,
): CanvasRenderFramePlan {
    return {
        connectionLayer: {
            selectedConnectionIds: new Set(
                frame.connectionLayer.selectedConnectionIds,
            ),
            options: {
                ...frame.connectionLayer.options,
                channelFocusedConnectionIds: new Set(
                    frame.connectionLayer.options.channelFocusedConnectionIds ??
                        [],
                ),
            },
        },
        nodeLayer: {
            selectedNodeIds: new Set(frame.nodeLayer.selectedNodeIds),
            options: reviveNodeOptions(frame.nodeLayer.options),
        },
        connectionPreview: frame.connectionPreview
            ? {
                  ...frame.connectionPreview,
                  obstacles:
                      frame.connectionPreview.obstacles.map(reviveNodeById),
                  destNode: frame.connectionPreview.destNode
                      ? reviveNodeById(frame.connectionPreview.destNode)
                      : undefined,
                  startNode: frame.connectionPreview.startNode
                      ? reviveNodeById(frame.connectionPreview.startNode)
                      : undefined,
              }
            : null,
        marqueeBounds: frame.marqueeBounds,
        failureTooltip: frame.failureTooltip
            ? {
                  node: reviveNodeById(frame.failureTooltip.node),
                  message: frame.failureTooltip.message,
              }
            : null,
        hoverTooltip: frame.hoverTooltip,
    };
}

function reviveNodeOptions(
    options: CanvasWorkerNodeRenderOptionsDto,
): CanvasRenderFramePlan['nodeLayer']['options'] {
    return {
        ...options,
        blockedTargetNodeIds: new Set(options.blockedTargetNodeIds ?? []),
        focusedPortKeys: new Set(options.focusedPortKeys ?? []),
        collectionIteratorPreviewLabels: new Map(
            options.collectionIteratorPreviewLabels ?? [],
        ),
        nodeIssueSummaries: new Map(
            (options.nodeIssueSummaries ?? []).map(([nodeId, summary]) => [
                nodeId,
                {
                    ...summary,
                    propertyIssues: new Map(summary.propertyIssues),
                } satisfies CanvasNodeIssueSummary,
            ]),
        ),
        validationFocus: options.validationFocus
            ? ({
                  ...options.validationFocus,
                  portKeys: new Set(options.validationFocus.portKeys ?? []),
              } satisfies CanvasNodeValidationFocus)
            : null,
    };
}

function reviveConnection(
    connection: CanvasWorkerConnectionDto,
): RenderedConnection {
    return {
        id: connection.id,
        routes: connection.routes.map((route) => ({
            ...route,
            problemSegments: route.problemSegments.map(reviveProblemSegment),
            visualProblemSegments:
                route.visualProblemSegments.map(reviveProblemSegment),
        })),
    } as unknown as RenderedConnection;
}

function reviveProblemSegment(
    segment: CanvasWorkerProblemSegmentDto,
): RouteProblemSegment {
    return {
        ...segment,
        obstacle: EMPTY_OBSTACLE,
    };
}

function reviveNode(node: CanvasWorkerNodeDto): RenderedNode {
    return {
        ...node,
        settingsRect: () => node.layout.toggleRect,
        contains: (x: number, y: number) =>
            x >= node.x &&
            x <= node.x + node.width &&
            y >= node.y &&
            y <= node.y + node.height,
        containsBuffered: (x: number, y: number, buffer: number) =>
            x >= node.x - buffer &&
            x <= node.x + node.width + buffer &&
            y >= node.y - buffer &&
            y <= node.y + node.height + buffer,
        snapPointFixedSide: (x: number, y: number, side: Side) =>
            snapPointFixedSide(node, x, y, side),
    } as RenderedNode;
}

function snapPointFixedSide(
    node: CanvasWorkerNodeDto,
    x: number,
    y: number,
    side: Side,
): { x: number; y: number; side: Side; pos: number } {
    switch (side) {
        case 'left': {
            const yy = clamp(y, node.y, node.y + node.height);
            return { x: node.x, y: yy, side, pos: yy - node.y };
        }
        case 'right': {
            const yy = clamp(y, node.y, node.y + node.height);
            return { x: node.x + node.width, y: yy, side, pos: yy - node.y };
        }
        case 'top': {
            const xx = clamp(x, node.x, node.x + node.width);
            return { x: xx, y: node.y, side, pos: xx - node.x };
        }
        case 'bottom': {
            const xx = clamp(x, node.x, node.x + node.width);
            return { x: xx, y: node.y + node.height, side, pos: xx - node.x };
        }
    }
}

function clamp(value: number, min: number, max: number): number {
    return Math.max(min, Math.min(max, value));
}

const EMPTY_OBSTACLE: PathfinderObstacle = {
    x: 0,
    y: 0,
    width: 0,
    height: 0,
    contains: () => false,
    containsBuffered: () => false,
    snapPointFixedSide: (_x, _y, side) => ({ x: 0, y: 0, side, pos: 0 }),
};
