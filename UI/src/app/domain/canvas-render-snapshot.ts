import { ConnectionModel } from './connection-model';
import { NodeModel } from './node-model';
import { PathfinderOptions } from './pathfinder';
import { CanvasDetailTier, CanvasRenderSnapshot, CanvasRouteValidationError } from './canvas-render-types';
import { RenderedConnection } from './canvas-render-connection-model';
import { addConnectedNodeId, portScopedKey } from './canvas-render-geometry-utilities';
import {
  buildConnectionLaneAssignments,
  buildConnectionShadeAssignments,
  connectionShadeForMode,
  DEFAULT_CONNECTION_LANE,
  DEFAULT_CONNECTION_SHADE,
} from './canvas-render-lane-shade';
import { RenderedNode } from './canvas-render-node-model';
import { SpatialIndex } from './canvas-render-spatial-index';

const EMPTY_NODE_ID_SET = new Set<number>();
const EMPTY_SECTION_KEY_SET = new Set<string>();

export function buildCanvasRenderSnapshot(
  nodes: readonly NodeModel[],
  connections: readonly ConnectionModel[],
  expandedNodeSections: ReadonlyMap<number, ReadonlySet<string>>,
  detailTier: CanvasDetailTier,
  pathfinderOptions: PathfinderOptions,
  readOnly: boolean,
): CanvasRenderSnapshot {
  const renderedNodes = nodes.map((node) => new RenderedNode(node, expandedNodeSections.get(node.id) ?? EMPTY_SECTION_KEY_SET));
  const nodeById = new Map(renderedNodes.map((node) => [node.id, node] as const));
  const summaryPortKeys = new Set<string>();
  const propertyPortKeys = new Set<string>();
  const connectedNodeIdsByNodeId = new Map<number, Set<number>>();
  const renderedConnections: RenderedConnection[] = [];
  const routeValidationErrors: CanvasRouteValidationError[] = [];
  const laneByConnectionId = buildConnectionLaneAssignments(connections);
  const shadeByConnectionId = buildConnectionShadeAssignments(connections, nodeById, detailTier);

  for (const connection of connections) {
    const from = nodeById.get(connection.from.id);
    const to = nodeById.get(connection.to.id);
    if (!from || !to) {
      continue;
    }

    const obstacles = renderedNodes.filter((node) => node.id !== from.id && node.id !== to.id);
    const renderedConnection = new RenderedConnection(
      connection,
      from,
      to,
      detailTier,
      pathfinderOptions,
      obstacles,
      null,
      false,
      laneByConnectionId.get(connection.id) ?? DEFAULT_CONNECTION_LANE,
      connectionShadeForMode(
        shadeByConnectionId.get(connection.id) ?? DEFAULT_CONNECTION_SHADE,
        from.renderStatus,
        readOnly,
      ),
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
      const sourcePropertyPort = from.outputPropertyPort(connection.anchor, mapping.sourceProperty);
      if (sourcePropertyPort) {
        propertyPortKeys.add(portScopedKey(from, sourcePropertyPort));
      }

      const targetPropertyPort = to.inputPropertyPort(mapping.targetProperty);
      if (targetPropertyPort) {
        propertyPortKeys.add(portScopedKey(to, targetPropertyPort));
      }
    }

    addConnectedNodeId(connectedNodeIdsByNodeId, from.id, to.id);
    addConnectedNodeId(connectedNodeIdsByNodeId, to.id, from.id);
  }

  const nodeIndex = new SpatialIndex<RenderedNode>(360);
  for (const node of renderedNodes) {
    nodeIndex.insert(node.interactionBounds(), node);
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
    connectionById: new Map(renderedConnections.map((connection) => [connection.id, connection] as const)),
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
    queryNodesAtPoint(x: number, y: number, exceptIds: ReadonlySet<number> = EMPTY_NODE_ID_SET): RenderedNode[] {
      return nodeIndex.queryPoint(x, y).filter((node) => !exceptIds.has(node.id));
    },
    queryConnectionsAtPoint(x: number, y: number): RenderedConnection[] {
      return connectionIndex.queryPoint(x, y);
    },
  };
}
