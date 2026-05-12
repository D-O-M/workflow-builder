import { ConnectionModel } from './connection-model';
import { BlueprintMarqueeSelectionRequest } from './blueprint-editor-store-contract';
import { NodeModel } from './node-model';

export interface BlueprintSelectionState {
  readonly selectedNodeIds: readonly number[];
  readonly selectedConnectionIds: readonly string[];
}

export function selectOnlyBlueprintNode(nodeId: number | null): BlueprintSelectionState {
  return nodeId === null
    ? emptyBlueprintSelection()
    : {
        selectedNodeIds: [nodeId],
        selectedConnectionIds: [],
      };
}

export function toggleBlueprintNodeSelection(request: {
  readonly selectedNodeIds: readonly number[];
  readonly connections: readonly ConnectionModel[];
  readonly nodeId: number;
}): BlueprintSelectionState {
  const nextNodeIds = new Set(request.selectedNodeIds);
  if (nextNodeIds.has(request.nodeId)) {
    nextNodeIds.delete(request.nodeId);
  } else {
    nextNodeIds.add(request.nodeId);
  }

  const selectedNodeIds = [...nextNodeIds];
  return {
    selectedNodeIds,
    selectedConnectionIds: collectInternalBlueprintConnectionIds(request.connections, new Set(selectedNodeIds)),
  };
}

export function selectOnlyBlueprintConnection(connectionId: string | null): BlueprintSelectionState {
  return connectionId === null
    ? emptyBlueprintSelection()
    : {
        selectedNodeIds: [],
        selectedConnectionIds: [connectionId],
      };
}

export function toggleBlueprintConnectionSelection(request: {
  readonly selectedNodeIds: readonly number[];
  readonly selectedConnectionIds: readonly string[];
  readonly connectionId: string;
}): BlueprintSelectionState {
  const nextConnectionIds = new Set(request.selectedConnectionIds);
  if (nextConnectionIds.has(request.connectionId)) {
    nextConnectionIds.delete(request.connectionId);
  } else {
    nextConnectionIds.add(request.connectionId);
  }

  return {
    selectedNodeIds: [...request.selectedNodeIds],
    selectedConnectionIds: [...nextConnectionIds],
  };
}

export function selectBlueprintNodesWithinRect(request: BlueprintMarqueeSelectionRequest & {
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
  readonly selectedNodeIds: readonly number[];
}): BlueprintSelectionState {
  const enclosedNodeIds = request.nodes
    .filter((node) => node.isFullyWithin(
      request.bounds.left,
      request.bounds.top,
      request.bounds.right,
      request.bounds.bottom,
    ))
    .map((node) => node.id);

  const selectedNodeIds = request.additive
    ? [...new Set([...request.selectedNodeIds, ...enclosedNodeIds])]
    : enclosedNodeIds;
  const nodeIdSet = new Set(selectedNodeIds);

  return {
    selectedNodeIds,
    selectedConnectionIds: collectInternalBlueprintConnectionIds(request.connections, nodeIdSet),
  };
}

export function collectInternalBlueprintConnectionIds(
  connections: readonly ConnectionModel[],
  selectedNodeIds: ReadonlySet<number>,
): string[] {
  return connections
    .filter((connection) =>
      selectedNodeIds.has(connection.from.id) && selectedNodeIds.has(connection.to.id))
    .map((connection) => connection.id);
}

export function emptyBlueprintSelection(): BlueprintSelectionState {
  return {
    selectedNodeIds: [],
    selectedConnectionIds: [],
  };
}
