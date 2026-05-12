import {
  CanvasBounds,
  CanvasRenderSnapshot,
  RenderedConnection,
  RenderedNode,
} from './canvas-render-model';
import { inflateBounds } from './canvas-render-geometry-utilities';

export interface CanvasRenderCullRequest {
  readonly snapshot: CanvasRenderSnapshot;
  readonly visibleWorldBounds: CanvasBounds | null;
  readonly worldPadding: number;
  readonly forcedNodeIds?: ReadonlySet<number>;
  readonly forcedConnectionIds?: ReadonlySet<string>;
}

export interface CanvasRenderCullPlan {
  readonly nodes: readonly RenderedNode[];
  readonly connections: readonly RenderedConnection[];
}

export function createCanvasRenderCullPlan(request: CanvasRenderCullRequest): CanvasRenderCullPlan {
  if (!request.visibleWorldBounds) {
    return {
      nodes: request.snapshot.nodes,
      connections: request.snapshot.connections,
    };
  }

  const paddedBounds = inflateBounds(request.visibleWorldBounds, request.worldPadding);
  const visibleNodeIds = new Set(request.snapshot.queryNodesInBounds(paddedBounds).map((node) => node.id));
  const visibleConnectionIds = new Set(request.snapshot.queryConnectionsInBounds(paddedBounds).map((connection) => connection.id));

  for (const nodeId of request.forcedNodeIds ?? []) {
    visibleNodeIds.add(nodeId);
  }

  for (const connectionId of request.forcedConnectionIds ?? []) {
    visibleConnectionIds.add(connectionId);
  }

  return {
    nodes: request.snapshot.nodes.filter((node) => visibleNodeIds.has(node.id)),
    connections: request.snapshot.connections.filter((connection) => visibleConnectionIds.has(connection.id)),
  };
}
