import {
    ConnectionModel,
    resolveEffectivePropertyMappings,
} from './connection-model';
import { NodeModel, Point } from './node-model';
import { PathfinderOptions, Segment } from './pathfinder';
import {
    CanvasConnectionLane,
    CanvasConnectionShade,
    CanvasDetailTier,
    CanvasRenderSnapshot,
    CanvasRouteValidationError,
    RenderedConnectionRoute,
} from './canvas-render-types';
import { RenderedConnection } from './canvas-render-connection-model';
import { buildConnectionVisualRoutes } from './canvas-render-connection-routing';
import {
    addConnectedNodeId,
    boundsIntersect,
    inflateBounds,
    portScopedKey,
    unionBounds,
} from './canvas-render-geometry-utilities';
import {
    buildConnectionLaneAssignments,
    buildConnectionShadeAssignments,
    connectionShadeForMode,
    DEFAULT_CONNECTION_LANE,
    DEFAULT_CONNECTION_SHADE,
} from './canvas-render-lane-shade';
import { RenderedNode } from './canvas-render-node-model';
import {
    CanvasConnectionRouteCache,
    CanvasRouteCacheLookup,
    obstacleSignature,
} from './canvas-render-route-cache';
import { SpatialIndex } from './canvas-render-spatial-index';

const EMPTY_NODE_ID_SET = new Set<number>();
const EMPTY_SECTION_KEY_SET = new Set<string>();

export interface CanvasRenderSnapshotBuildOptions {
    readonly transientNodePositions?: ReadonlyMap<number, Point> | null;
    readonly routeCache?: CanvasConnectionRouteCache | null;
}

export function buildCanvasRenderSnapshot(
    nodes: readonly NodeModel[],
    connections: readonly ConnectionModel[],
    expandedNodeSections: ReadonlyMap<number, ReadonlySet<string>>,
    detailTier: CanvasDetailTier,
    pathfinderOptions: PathfinderOptions,
    readOnly: boolean,
    options: CanvasRenderSnapshotBuildOptions = {},
): CanvasRenderSnapshot {
    const renderedNodes = nodes.map(
        (node) =>
            new RenderedNode(
                nodeWithTransientPosition(
                    node,
                    options.transientNodePositions ?? null,
                ),
                expandedNodeSections.get(node.id) ?? EMPTY_SECTION_KEY_SET,
            ),
    );
    const nodeById = new Map(
        renderedNodes.map((node) => [node.id, node] as const),
    );
    const nodeIndex = new SpatialIndex<RenderedNode>(360);
    for (const node of renderedNodes) {
        nodeIndex.insert(node.interactionBounds(), node);
    }

    const summaryPortKeys = new Set<string>();
    const propertyPortKeys = new Set<string>();
    const connectedNodeIdsByNodeId = new Map<number, Set<number>>();
    const renderedConnections: RenderedConnection[] = [];
    const routeValidationErrors: CanvasRouteValidationError[] = [];
    const laneByConnectionId = buildConnectionLaneAssignments(connections);
    const shadeByConnectionId = buildConnectionShadeAssignments(
        connections,
        nodeById,
        detailTier,
    );

    for (const connection of connections) {
        const from = nodeById.get(connection.from.id);
        const to = nodeById.get(connection.to.id);
        if (!from || !to) {
            continue;
        }

        const exceptNodeIds = new Set([from.id, to.id]);
        const relevantObstacles = relevantRouteObstacles(
            from,
            to,
            nodeIndex,
            pathfinderOptions,
            exceptNodeIds,
        );
        const effectiveMappings = resolveEffectivePropertyMappings(connection);
        const lane =
            laneByConnectionId.get(connection.id) ?? DEFAULT_CONNECTION_LANE;
        const shade = connectionShadeForMode(
            shadeByConnectionId.get(connection.id) ?? DEFAULT_CONNECTION_SHADE,
            connection.pathNotTaken ? 'Skipped' : from.renderStatus,
            readOnly,
        );
        const routes = routeConnection({
            connection,
            from,
            to,
            detailTier,
            pathfinderOptions,
            relevantObstacles,
            allNodes: renderedNodes,
            nodeIndex,
            exceptNodeIds,
            effectiveMappings,
            lane,
            shade,
            routeCache: options.routeCache ?? null,
        });
        const renderedConnection = new RenderedConnection(
            connection,
            from,
            to,
            detailTier,
            pathfinderOptions,
            relevantObstacles,
            null,
            false,
            lane,
            shade,
            routes,
        );
        renderedConnections.push(renderedConnection);
        routeValidationErrors.push(...renderedConnection.routeValidationErrors);

        const sourceSummaryPort = from.outputSummaryPort(connection.anchor);
        if (sourceSummaryPort) {
            summaryPortKeys.add(portScopedKey(from, sourceSummaryPort));
        }

        const targetSummaryPort = to.inputSummaryPort();
        if (targetSummaryPort) {
            summaryPortKeys.add(portScopedKey(to, targetSummaryPort));
        }

        for (const mapping of renderedConnection.effectiveMappings) {
            const sourcePropertyPort = from.outputPropertyPort(
                connection.anchor,
                mapping.sourceProperty,
            );
            if (sourcePropertyPort) {
                propertyPortKeys.add(portScopedKey(from, sourcePropertyPort));
            }

            const targetPropertyPort = to.inputPropertyPort(
                mapping.targetProperty,
            );
            if (targetPropertyPort) {
                propertyPortKeys.add(portScopedKey(to, targetPropertyPort));
            }
        }

        addConnectedNodeId(connectedNodeIdsByNodeId, from.id, to.id);
        addConnectedNodeId(connectedNodeIdsByNodeId, to.id, from.id);
    }

    const connectionIndex = new SpatialIndex<RenderedConnection>(360);
    for (const connection of renderedConnections) {
        if (connection.routeBounds) {
            connectionIndex.insert(connection.routeBounds, connection);
        }
    }

    return {
        detailTier,
        nodes: renderedNodes,
        connections: renderedConnections,
        routeValidationErrors,
        nodeById,
        connectionById: new Map(
            renderedConnections.map(
                (connection) => [connection.id, connection] as const,
            ),
        ),
        portConnectivity: {
            summaryPortKeys,
            propertyPortKeys,
        },
        getConnectedNodeIds(nodeId: number): ReadonlySet<number> {
            return connectedNodeIdsByNodeId.get(nodeId) ?? EMPTY_NODE_ID_SET;
        },
        getConnectedNodes(nodeId: number): RenderedNode[] {
            const nodeIds = connectedNodeIdsByNodeId.get(nodeId);
            if (!nodeIds || nodeIds.size === 0) {
                return [];
            }

            return [...nodeIds]
                .map((connectedId) => nodeById.get(connectedId))
                .filter((node): node is RenderedNode => !!node);
        },
        queryNodesAtPoint(
            x: number,
            y: number,
            exceptIds: ReadonlySet<number> = EMPTY_NODE_ID_SET,
        ): RenderedNode[] {
            return nodeIndex
                .queryPoint(x, y)
                .filter((node) => !exceptIds.has(node.id));
        },
        queryNodesInBounds(
            bounds,
            exceptIds: ReadonlySet<number> = EMPTY_NODE_ID_SET,
        ): RenderedNode[] {
            return nodeIndex
                .queryBounds(bounds)
                .filter(
                    (node) =>
                        !exceptIds.has(node.id) &&
                        boundsIntersect(node.interactionBounds(), bounds),
                );
        },
        queryConnectionsAtPoint(x: number, y: number): RenderedConnection[] {
            return connectionIndex.queryPoint(x, y);
        },
        queryConnectionsInBounds(bounds): RenderedConnection[] {
            return connectionIndex
                .queryBounds(bounds)
                .filter(
                    (connection) =>
                        !!connection.routeBounds &&
                        boundsIntersect(connection.routeBounds, bounds),
                );
        },
    };
}

interface RouteConnectionRequest {
    readonly connection: ConnectionModel;
    readonly from: RenderedNode;
    readonly to: RenderedNode;
    readonly detailTier: CanvasDetailTier;
    readonly pathfinderOptions: PathfinderOptions;
    readonly relevantObstacles: readonly RenderedNode[];
    readonly allNodes: readonly RenderedNode[];
    readonly nodeIndex: SpatialIndex<RenderedNode>;
    readonly exceptNodeIds: ReadonlySet<number>;
    readonly effectiveMappings: ReturnType<
        typeof resolveEffectivePropertyMappings
    >;
    readonly lane: CanvasConnectionLane;
    readonly shade: CanvasConnectionShade;
    readonly routeCache: CanvasConnectionRouteCache | null;
}

function routeConnection(
    request: RouteConnectionRequest,
): readonly RenderedConnectionRoute[] {
    const lookup = routeCacheLookup(request, request.relevantObstacles);
    const cachedRoutes = request.routeCache?.get(lookup) ?? null;
    if (
        cachedRoutes &&
        !routesCrossOmittedObstacles(
            request,
            cachedRoutes,
            request.relevantObstacles,
        )
    ) {
        return cachedRoutes;
    }

    const relevantRoutes =
        cachedRoutes ??
        buildConnectionVisualRoutes(
            request.from,
            request.to,
            request.connection.anchor,
            request.effectiveMappings,
            request.detailTier,
            request.pathfinderOptions,
            [...request.relevantObstacles],
            request.lane,
            request.shade,
        );

    if (
        !routesCrossOmittedObstacles(
            request,
            relevantRoutes,
            request.relevantObstacles,
        )
    ) {
        request.routeCache?.set(lookup, relevantRoutes);
        return relevantRoutes;
    }

    const allObstacles = request.allNodes.filter(
        (node) => !request.exceptNodeIds.has(node.id),
    );
    const fullLookup = routeCacheLookup(request, allObstacles);
    const fullCachedRoutes = request.routeCache?.get(fullLookup) ?? null;
    if (fullCachedRoutes) {
        return fullCachedRoutes;
    }

    const fullRoutes = buildConnectionVisualRoutes(
        request.from,
        request.to,
        request.connection.anchor,
        request.effectiveMappings,
        request.detailTier,
        request.pathfinderOptions,
        allObstacles,
        request.lane,
        request.shade,
    );
    request.routeCache?.set(fullLookup, fullRoutes);
    return fullRoutes;
}

function routeCacheLookup(
    request: RouteConnectionRequest,
    obstacles: readonly RenderedNode[],
): CanvasRouteCacheLookup {
    return {
        connectionId: request.connection.id,
        anchor: request.connection.anchor,
        side: request.connection.side,
        pos: request.connection.pos,
        from: request.from,
        to: request.to,
        detailTier: request.detailTier,
        pathfinderOptions: request.pathfinderOptions,
        effectiveMappings: request.effectiveMappings,
        lane: request.lane,
        shade: request.shade,
        obstacleSignature: obstacleSignature(obstacles),
    };
}

function nodeWithTransientPosition(
    node: NodeModel,
    transientNodePositions: ReadonlyMap<number, Point> | null,
): NodeModel {
    const position = transientNodePositions?.get(node.id);
    return position ? cloneNodeAtPosition(node, position) : node;
}

function cloneNodeAtPosition(node: NodeModel, position: Point): NodeModel {
    return new NodeModel(
        node.id,
        node.taskDefinitionId,
        node.allowedMajor,
        node.allowedMinor,
        node.resolvedVersionId,
        node.resolvedVersion,
        node.name,
        position.x,
        position.y,
        node.width,
        node.height,
        node.inputChannelName,
        [...node.outputChannelNames],
        node.hasUpgradeWarning,
        node.renderStatus,
        node.failureMessage,
        { ...node.inputProperties },
        Object.fromEntries(
            Object.entries(node.outputChannelProperties).map(
                ([channelName, properties]) => [channelName, { ...properties }],
            ),
        ),
        { ...node.designTimeInputOverrides },
        [...node.inputPropertyDefinitions],
        node.inputChannelDisplayName,
        [...node.outputChannelDisplayNames],
        Object.fromEntries(
            Object.entries(node.outputChannelPropertyDefinitions).map(
                ([channelName, properties]) => [channelName, [...properties]],
            ),
        ),
        node.expanded,
        node.nodeKind,
        node.collectionIteratorItemKind,
        node.instanceId,
        node.conditionalRule,
    );
}

function relevantRouteObstacles(
    from: RenderedNode,
    to: RenderedNode,
    nodeIndex: SpatialIndex<RenderedNode>,
    pathfinderOptions: PathfinderOptions,
    exceptNodeIds: ReadonlySet<number>,
): RenderedNode[] {
    const bounds = inflateBounds(
        unionBounds(from.interactionBounds(), to.interactionBounds()),
        routeObstacleQueryPadding(from, to, pathfinderOptions),
    );
    return queryNodesInBounds(nodeIndex, bounds, exceptNodeIds);
}

function routeObstacleQueryPadding(
    from: RenderedNode,
    to: RenderedNode,
    pathfinderOptions: PathfinderOptions,
): number {
    const fromCenterX = from.x + from.width / 2;
    const fromCenterY = from.y + from.height / 2;
    const toCenterX = to.x + to.width / 2;
    const toCenterY = to.y + to.height / 2;
    const endpointDistance =
        Math.abs(fromCenterX - toCenterX) + Math.abs(fromCenterY - toCenterY);
    return Math.max(
        360,
        pathfinderOptions.nodeMargin + pathfinderOptions.gridSize * 18,
        endpointDistance * 0.12,
    );
}

function routesCrossOmittedObstacles(
    request: RouteConnectionRequest,
    routes: readonly RenderedConnectionRoute[],
    relevantObstacles: readonly RenderedNode[],
): boolean {
    const relevantNodeIds = new Set(relevantObstacles.map((node) => node.id));
    for (const route of routes) {
        if (!route.canonicalBounds) {
            continue;
        }

        const candidates = queryNodesInBounds(
            request.nodeIndex,
            inflateBounds(
                route.canonicalBounds,
                request.pathfinderOptions.nodeMargin,
            ),
            request.exceptNodeIds,
        );
        for (const node of candidates) {
            if (relevantNodeIds.has(node.id)) {
                continue;
            }

            if (
                segmentsCrossNodeBuffer(
                    route.segments,
                    node,
                    request.pathfinderOptions.nodeMargin,
                )
            ) {
                return true;
            }
        }
    }

    return false;
}

function queryNodesInBounds(
    nodeIndex: SpatialIndex<RenderedNode>,
    bounds: {
        readonly left: number;
        readonly top: number;
        readonly right: number;
        readonly bottom: number;
    },
    exceptNodeIds: ReadonlySet<number>,
): RenderedNode[] {
    return nodeIndex
        .queryBounds(bounds)
        .filter(
            (node) =>
                !exceptNodeIds.has(node.id) &&
                boundsIntersect(node.interactionBounds(), bounds),
        );
}

function segmentsCrossNodeBuffer(
    segments: readonly Segment[],
    node: RenderedNode,
    buffer: number,
): boolean {
    const left = node.x - buffer;
    const right = node.x + node.width + buffer;
    const top = node.y - buffer;
    const bottom = node.y + node.height + buffer;

    for (const segment of segments) {
        if (segment.y1 === segment.y2) {
            const y = segment.y1;
            if (y <= top || y >= bottom) {
                continue;
            }

            const start = Math.min(segment.x1, segment.x2);
            const end = Math.max(segment.x1, segment.x2);
            if (Math.max(start, left) < Math.min(end, right)) {
                return true;
            }
        } else if (segment.x1 === segment.x2) {
            const x = segment.x1;
            if (x <= left || x >= right) {
                continue;
            }

            const start = Math.min(segment.y1, segment.y2);
            const end = Math.max(segment.y1, segment.y2);
            if (Math.max(start, top) < Math.min(end, bottom)) {
                return true;
            }
        } else {
            return true;
        }
    }

    return false;
}
