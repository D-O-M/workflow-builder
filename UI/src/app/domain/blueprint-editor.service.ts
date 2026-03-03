import { Injectable, signal } from '@angular/core';
import { ConnectionModel } from './connection-model';
import { NodeModel, Point, Side } from './node-model';

@Injectable({ providedIn: 'root' })
export class BlueprintEditorService {
  readonly nodes = signal<NodeModel[]>([]);
  readonly connections = signal<ConnectionModel[]>([]);

  createNode(name: string, coordinates: Point, snap: (v: number) => number): NodeModel {
    const node = new NodeModel(
      Date.now(),
      name,
      snap(coordinates.x),
      snap(coordinates.y),
      240,
      160,
    );

    this.nodes.update((n) => [...n, node]);
    return node;
  }

  updateNodeCoordinates(nodeId: number, newCoordinates: Point, snap: (v: number) => number): void {
    const nx = snap(newCoordinates.x);
    const ny = snap(newCoordinates.y);

    this.nodes.update((nodes) =>
      nodes.map((n) => (n.id === nodeId ? new NodeModel(n.id, n.name, nx, ny, n.width, n.height) : n)),
    );

    // keep connection endpoints consistent as they reference node instances
    this.rebindConnections();
  }

  createConnection(sourceNodeId: number, destinationNodeId: number, anchor: number, side: Side, position: number, snap: (v: number) => number): void {
    const from = this.getNodeById(sourceNodeId);
    const to = this.getNodeById(destinationNodeId);
    const snappedPosition = snap(position);

    this.connections.update((c) => [...c, new ConnectionModel(from, to, anchor, side, snappedPosition)]);
  }

  getNodeById(id: number): NodeModel {
    return this.nodes().find((n) => n.id === id)!;
  }

  getNodes(...except: Array<number | undefined>): NodeModel[] {
    return this.nodes().filter((node) => !except.includes(node.id));
  }

  getNodeByCoordinates(coordinates: Point, ...except: NodeModel[]): NodeModel | undefined {
    return this.nodes().find((node) => {
      if (except.includes(node) || except.some((n) => n.contains(coordinates.x, coordinates.y))) {
        return false;
      }

      return node.contains(coordinates.x, coordinates.y);
    });
  }

  getNodesConnectedTo(node: NodeModel): NodeModel[] {
    return [...new Set(
      this.connections()
        .filter((c) => c.from === node || c.to === node)
        .flatMap((c) => [c.from, c.to])
        .filter((n) => !!n),
    )];
  }

  private rebindConnections(): void {
    const nodeMap = new Map(this.nodes().map((n) => [n.id, n] as const));
    this.connections.update((connections) =>
      connections.map((c) => new ConnectionModel(nodeMap.get(c.from.id)!, nodeMap.get(c.to.id)!, c.anchor, c.side, c.pos)),
    );
  }
}
