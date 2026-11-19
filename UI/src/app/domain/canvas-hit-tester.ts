import { CanvasRenderSnapshot, RenderedConnection, RenderedNode } from './canvas-render-model';
import { NodePortDefinition, Point, TaskPropertyType } from './node-model';

export interface CanvasHitTestContext {
  readonly snapshot: CanvasRenderSnapshot;
  readonly zoomLevel: number;
}

export interface CanvasConnectionRouteHit {
  readonly connection: RenderedConnection;
  readonly routeIndex: number;
  readonly distance: number;
}

export interface CanvasHoveredConnectionRouteState {
  readonly connectionId: string;
  readonly routeIndex: number;
}

export interface CanvasConnectionDragContext {
  readonly from: number;
  readonly anchor: number;
  readonly sourceProperty?: string | null;
}

export class CanvasHitTester {
  nodeAt(
    snapshot: CanvasRenderSnapshot,
    coordinates: Point,
    except: readonly RenderedNode[] = [],
  ): RenderedNode | undefined {
    const exceptIds = new Set(except.map((node) => node.id));
    return [...snapshot.queryNodesAtPoint(coordinates.x, coordinates.y, exceptIds)]
      .reverse()
      .find((node) => node.containsInteractionPoint(coordinates.x, coordinates.y));
  }

  connectionRouteAt(context: CanvasHitTestContext, coordinates: Point): CanvasConnectionRouteHit | undefined {
    const tolerance = 10 / context.zoomLevel;
    let nearestHit: CanvasConnectionRouteHit | undefined;
    let nearestDistance = Number.POSITIVE_INFINITY;
    for (const connection of context.snapshot.queryConnectionsAtPoint(coordinates.x, coordinates.y)) {
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

  connectionAt(context: CanvasHitTestContext, coordinates: Point): RenderedConnection | undefined {
    return this.connectionRouteAt(context, coordinates)?.connection;
  }

  connectionBadgeAt(connection: RenderedConnection, coordinates: Point, zoomLevel: number): RenderedConnection | undefined {
    const badgeRadius = 18 / zoomLevel;
    return connection.badgeHitTest(coordinates.x, coordinates.y, badgeRadius)
      ? connection
      : undefined;
  }

  portAt(node: RenderedNode, coordinates: Point, zoomLevel: number): NodePortDefinition | null {
    const extraHitRadius = Math.max(4, 12 / zoomLevel - 7);
    return node.portHit(coordinates.x, coordinates.y, extraHitRadius);
  }

  inputPortAt(node: RenderedNode, coordinates: Point, zoomLevel: number): NodePortDefinition | null {
    const port = this.portAt(node, coordinates, zoomLevel);
    if (port && (port.kind === 'input-summary' || port.kind === 'input-property')) {
      return port;
    }

    return node.inputSummaryPort();
  }

  connectionNeighborIds(snapshot: CanvasRenderSnapshot, drag: CanvasConnectionDragContext): ReadonlySet<number> {
    if (snapshot.nodeById.get(drag.from)?.base.nodeKind !== 'ConditionalBranch') return snapshot.getConnectedNodeIds(drag.from);
    // A shared successor may be wired once per mutually exclusive outcome.
    const blocked = new Set<number>();
    for (const edge of snapshot.connections) {
      if (edge.to.id === drag.from) blocked.add(edge.from.id);
      if (edge.from.id === drag.from && edge.anchor === drag.anchor) blocked.add(edge.to.id);
    }
    return blocked;
  }

  blockedTargetNodeIds(snapshot: CanvasRenderSnapshot, drag: CanvasConnectionDragContext): ReadonlySet<number> {
    const blockedNodeIds = new Set([drag.from, ...this.connectionNeighborIds(snapshot, drag)]);
    const sourceNode = snapshot.nodeById.get(drag.from) ?? null;

    for (const node of snapshot.nodes) {
      if (node.base.nodeKind !== 'CollectionIterator' || blockedNodeIds.has(node.id)) {
        continue;
      }

      if (!this.canDragBindToCollectionIterator(sourceNode, drag)) {
        blockedNodeIds.add(node.id);
      }
    }

    return blockedNodeIds;
  }

  collectionIteratorPreviewLabels(
    snapshot: CanvasRenderSnapshot,
    drag: CanvasConnectionDragContext,
  ): ReadonlyMap<number, string> {
    const sourceNode = snapshot.nodeById.get(drag.from) ?? null;
    const collectionKind = this.draggedCollectionKind(sourceNode, drag);
    if (!collectionKind) {
      return new Map<number, string>();
    }

    const itemKind = collectionKind.slice(0, -2);
    const blockedNodeIds = new Set([drag.from, ...this.connectionNeighborIds(snapshot, drag)]);
    const labels = new Map<number, string>();
    for (const node of snapshot.nodes) {
      if (node.base.nodeKind === 'CollectionIterator' && !blockedNodeIds.has(node.id)) {
        labels.set(node.id, `${collectionKind} -> ${itemKind}`);
      }
    }

    return labels;
  }

  canDragBindToCollectionIterator(sourceNode: RenderedNode | null, drag: CanvasConnectionDragContext): boolean {
    return this.draggedCollectionKind(sourceNode, drag) !== null;
  }

  channelFocusedConnectionIds(
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

  private draggedCollectionKind(sourceNode: RenderedNode | null, drag: CanvasConnectionDragContext): `${string}[]` | null {
    if (!sourceNode) {
      return null;
    }

    const sourceProperties = sourceNode.outputPropertiesForAnchor(drag.anchor);
    if (drag.sourceProperty) {
      const sourceKind = sourceProperties[drag.sourceProperty];
      return this.isCollectionKind(sourceKind) ? sourceKind : null;
    }

    return Object.entries(sourceProperties)
      .filter(([, kind]) => this.isCollectionKind(kind))
      .sort(([left], [right]) => left.localeCompare(right))[0]?.[1] as `${string}[]` | undefined ?? null;
  }

  private isCollectionKind(kind: TaskPropertyType | string | null | undefined): kind is `${string}[]` {
    return typeof kind === 'string' && kind.endsWith('[]');
  }
}
