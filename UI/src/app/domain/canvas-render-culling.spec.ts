import { describe, expect, it, vi } from 'vitest';
import { createCanvasRenderCullPlan } from './canvas-render-culling';
import {
  CanvasBounds,
  CanvasRenderSnapshot,
  RenderedConnection,
  RenderedNode,
} from './canvas-render-model';

function node(id: number): RenderedNode {
  return { id } as RenderedNode;
}

function connection(id: string): RenderedConnection {
  return { id } as RenderedConnection;
}

function snapshot(
  nodes: readonly RenderedNode[],
  connections: readonly RenderedConnection[],
  visibleNodes: readonly RenderedNode[],
  visibleConnections: readonly RenderedConnection[],
): CanvasRenderSnapshot {
  return {
    detailTier: 'full',
    nodes: [...nodes],
    connections: [...connections],
    routeValidationErrors: [],
    nodeById: new Map(nodes.map((candidate) => [candidate.id, candidate])),
    connectionById: new Map(connections.map((candidate) => [candidate.id, candidate])),
    portConnectivity: {
      summaryPortKeys: new Set(),
      propertyPortKeys: new Set(),
    },
    getConnectedNodeIds: vi.fn(() => new Set()),
    getConnectedNodes: vi.fn(() => []),
    queryNodesAtPoint: vi.fn(() => []),
    queryNodesInBounds: vi.fn(() => [...visibleNodes]),
    queryConnectionsAtPoint: vi.fn(() => []),
    queryConnectionsInBounds: vi.fn(() => [...visibleConnections]),
  };
}

describe('createCanvasRenderCullPlan', () => {
  const visibleWorldBounds: CanvasBounds = { left: 0, top: 0, right: 100, bottom: 100 };

  it('returns the full render graph when culling is disabled', () => {
    const visibleNode = node(1);
    const offscreenNode = node(2);
    const visibleConnection = connection('visible');
    const offscreenConnection = connection('offscreen');

    const plan = createCanvasRenderCullPlan({
      snapshot: snapshot([visibleNode, offscreenNode], [visibleConnection, offscreenConnection], [visibleNode], [visibleConnection]),
      visibleWorldBounds: null,
      worldPadding: 0,
    });

    expect(plan.nodes).toEqual([visibleNode, offscreenNode]);
    expect(plan.connections).toEqual([visibleConnection, offscreenConnection]);
  });

  it('draws visible elements and forced focused elements in original order', () => {
    const firstOffscreenNode = node(1);
    const visibleNode = node(2);
    const secondOffscreenNode = node(3);
    const firstOffscreenConnection = connection('offscreen-a');
    const visibleConnection = connection('visible');
    const secondOffscreenConnection = connection('offscreen-b');

    const plan = createCanvasRenderCullPlan({
      snapshot: snapshot(
        [firstOffscreenNode, visibleNode, secondOffscreenNode],
        [firstOffscreenConnection, visibleConnection, secondOffscreenConnection],
        [visibleNode],
        [visibleConnection],
      ),
      visibleWorldBounds,
      worldPadding: 80,
      forcedNodeIds: new Set([secondOffscreenNode.id]),
      forcedConnectionIds: new Set([firstOffscreenConnection.id]),
    });

    expect(plan.nodes).toEqual([visibleNode, secondOffscreenNode]);
    expect(plan.connections).toEqual([firstOffscreenConnection, visibleConnection]);
  });
});
