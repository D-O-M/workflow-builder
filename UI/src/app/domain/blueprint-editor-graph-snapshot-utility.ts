import { ConnectionModel, ConnectionPropertyMappingModel } from './connection-model';
import {
  BlueprintNodeSnapshotModel,
  instantiateBlueprintNodeSnapshot,
  snapshotBlueprintNodeModel,
} from './blueprint-editor-node-factory';
import { createBlueprintConnectionModel } from './blueprint-editor-connection-policy';
import { NodeModel } from './node-model';

export type BlueprintGraphNodeSnapshot = BlueprintNodeSnapshotModel;

export interface BlueprintGraphConnectionSnapshot {
  readonly id: string;
  readonly fromId: number;
  readonly toId: number;
  readonly anchor: number;
  readonly side: ConnectionModel['side'];
  readonly pos: number;
  readonly propertyMappings: readonly ConnectionPropertyMappingModel[];
}

export interface BlueprintGraphSnapshot {
  readonly nodes: readonly BlueprintGraphNodeSnapshot[];
  readonly connections: readonly BlueprintGraphConnectionSnapshot[];
  readonly selectedNodeIds: readonly number[];
  readonly selectedConnectionIds: readonly string[];
}

export interface BlueprintClipboardSnapshot {
  readonly nodes: readonly BlueprintGraphNodeSnapshot[];
  readonly connections: readonly BlueprintGraphConnectionSnapshot[];
}

export interface BlueprintGraphSnapshotRestoreResult {
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
  readonly selectedNodeIds: readonly number[];
  readonly selectedConnectionIds: readonly string[];
}

export function snapshotBlueprintGraph(request: {
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
  readonly selectedNodeIds: readonly number[];
  readonly selectedConnectionIds: readonly string[];
}): BlueprintGraphSnapshot {
  return {
    nodes: request.nodes.map((node) => snapshotBlueprintNodeModel(node)),
    connections: request.connections.map((connection) => snapshotBlueprintConnection(connection)),
    selectedNodeIds: [...request.selectedNodeIds],
    selectedConnectionIds: [...request.selectedConnectionIds],
  };
}

export function snapshotBlueprintClipboard(request: {
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
  readonly selectedNodeIds: ReadonlySet<number>;
}): BlueprintClipboardSnapshot {
  return {
    nodes: request.nodes
      .filter((node) => request.selectedNodeIds.has(node.id))
      .map((node) => snapshotBlueprintNodeModel(node)),
    connections: request.connections
      .filter((connection) =>
        request.selectedNodeIds.has(connection.from.id) && request.selectedNodeIds.has(connection.to.id))
      .map((connection) => snapshotBlueprintConnection(connection)),
  };
}

export function snapshotBlueprintConnection(connection: ConnectionModel): BlueprintGraphConnectionSnapshot {
  return {
    id: connection.id,
    fromId: connection.from.id,
    toId: connection.to.id,
    anchor: connection.anchor,
    side: connection.side,
    pos: connection.pos,
    propertyMappings: connection.propertyMappings.map((mapping) => ({ ...mapping })),
  };
}

export function restoreBlueprintGraphSnapshot(snapshot: BlueprintGraphSnapshot): BlueprintGraphSnapshotRestoreResult {
  const nodes = snapshot.nodes.map((node) => instantiateBlueprintNodeSnapshot(node));
  const nodeMap = new Map(nodes.map((node) => [node.id, node] as const));
  const connections = instantiateBlueprintConnectionSnapshots(snapshot.connections, nodeMap);

  return {
    nodes,
    connections,
    selectedNodeIds: snapshot.selectedNodeIds.filter((id) => nodeMap.has(id)),
    selectedConnectionIds: snapshot.selectedConnectionIds.filter((id) =>
      connections.some((connection) => connection.id === id)),
  };
}

export function instantiateBlueprintConnectionSnapshots(
  snapshots: readonly BlueprintGraphConnectionSnapshot[],
  nodeMap: ReadonlyMap<number, NodeModel>,
): ConnectionModel[] {
  return snapshots
    .map((connection) => {
      const from = nodeMap.get(connection.fromId);
      const to = nodeMap.get(connection.toId);
      if (!from || !to) {
        return null;
      }

      return createBlueprintConnectionModel({
        source: { node: from, anchor: connection.anchor },
        destination: { node: to, side: connection.side, position: connection.pos },
        id: connection.id,
        propertyMappings: connection.propertyMappings,
      });
    })
    .filter((connection): connection is ConnectionModel => connection !== null);
}
