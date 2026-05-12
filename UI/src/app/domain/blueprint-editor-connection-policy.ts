import { ConnectionModel, ConnectionPropertyMappingModel } from './connection-model';
import { NodeModel, TaskPropertyBaseType, TaskPropertyType } from './node-model';

export interface BlueprintResolvedConnectionCreateRequest {
  readonly source: {
    readonly node: NodeModel;
    readonly anchor: number;
  };
  readonly destination: {
    readonly node: NodeModel;
    readonly side: ConnectionModel['side'];
    readonly position: number;
  };
  readonly id?: string;
  readonly propertyMappings?: readonly ConnectionPropertyMappingModel[];
}

export interface BlueprintConnectionVersionReconcileRequest {
  readonly connection: ConnectionModel;
  readonly previousNode: NodeModel;
  readonly nextNode: NodeModel;
  readonly nodeMap: ReadonlyMap<number, NodeModel>;
}

export function createBlueprintConnectionModel(request: BlueprintResolvedConnectionCreateRequest): ConnectionModel {
  return new ConnectionModel(
    request.source.node,
    request.destination.node,
    request.source.anchor,
    request.destination.side,
    request.destination.position,
    request.id ?? crypto.randomUUID(),
    request.propertyMappings?.map((mapping) => ({ ...mapping })) ?? [],
  );
}

export function rebindBlueprintConnections(
  connections: readonly ConnectionModel[],
  nodes: readonly NodeModel[],
): ConnectionModel[] {
  const nodeMap = new Map(nodes.map((node) => [node.id, node] as const));
  return connections
    .map((connection) => {
      const from = nodeMap.get(connection.from.id);
      const to = nodeMap.get(connection.to.id);
      if (!from || !to || from.id === to.id) {
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

export function canCreateBlueprintConnection(from: NodeModel, to: NodeModel): boolean {
  if (from.isDataBridge && to.isDataBridge) {
    return false;
  }

  if (to.isDataBridge) {
    return !from.isDataBridge && to.dataBridgeKind === 'output';
  }

  if (from.isDataBridge) {
    return from.dataBridgeKind === 'input' && !to.isDataBridge;
  }

  return true;
}

export function isDuplicateBlueprintConnection(
  connections: readonly ConnectionModel[],
  request: BlueprintResolvedConnectionCreateRequest,
): boolean {
  return connections.some((connection) =>
    connection.from.id === request.source.node.id &&
    connection.to.id === request.destination.node.id &&
    connection.anchor === request.source.anchor &&
    connection.side === request.destination.side &&
    connection.pos === request.destination.position);
}

export function getDefaultBlueprintConnectionMappings(
  from: NodeModel,
  to: NodeModel,
  anchor: number,
): ConnectionPropertyMappingModel[] {
  const sourceProperties = from.outputPropertiesForAnchor(anchor);
  if (to.nodeKind === 'CollectionIterator') {
    const collectionProperty = Object.entries(sourceProperties)
      .filter(([, kind]) => isBlueprintCollectionKind(kind))
      .sort(([left], [right]) => left.localeCompare(right))[0];
    return collectionProperty
      ? [{
          sourceProperty: collectionProperty[0],
          targetProperty: 'items',
        }]
      : [];
  }

  return Object.keys(sourceProperties)
    .filter((property) => property in to.inputProperties && sourceProperties[property] === to.inputProperties[property])
    .sort((left, right) => left.localeCompare(right))
    .map((property) => ({
      sourceProperty: property,
      targetProperty: property,
    }));
}

export function filterCompatibleBlueprintConnectionMappings(
  mappings: readonly ConnectionPropertyMappingModel[],
  from: NodeModel,
  to: NodeModel,
  anchor: number,
): ConnectionPropertyMappingModel[] {
  const sourceProperties = from.outputPropertiesForAnchor(anchor);
  return mappings
    .filter((mapping) =>
      Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty) &&
      Object.prototype.hasOwnProperty.call(to.inputProperties, mapping.targetProperty) &&
      sourceProperties[mapping.sourceProperty] === to.inputProperties[mapping.targetProperty])
    .map((mapping) => ({ ...mapping }));
}

export function reconcileBlueprintConnectionForNodeVersion(
  request: BlueprintConnectionVersionReconcileRequest,
): ConnectionModel | null {
  const from = request.connection.from.id === request.previousNode.id
    ? request.nextNode
    : request.nodeMap.get(request.connection.from.id);
  const to = request.connection.to.id === request.previousNode.id
    ? request.nextNode
    : request.nodeMap.get(request.connection.to.id);
  if (!from || !to || from.id === to.id) {
    return null;
  }

  if (request.connection.to.id === request.previousNode.id && !request.nextNode.inputChannelName) {
    return null;
  }

  let anchor = request.connection.anchor;
  if (request.connection.from.id === request.previousNode.id) {
    const previousChannelName = request.previousNode.outputChannelNameForAnchor(request.connection.anchor);
    const nextAnchor = previousChannelName ? outputAnchorIndexByBlueprintChannel(request.nextNode, previousChannelName) : null;
    if (nextAnchor === null) {
      return null;
    }

    anchor = nextAnchor;
  }

  return createBlueprintConnectionModel({
    source: { node: from, anchor },
    destination: {
      node: to,
      side: request.connection.side,
      position: request.connection.to.id === request.previousNode.id
        ? to.inputAnchorSnap()?.pos ?? request.connection.pos
        : request.connection.pos,
    },
    id: request.connection.id,
    propertyMappings: filterCompatibleBlueprintConnectionMappings(
      request.connection.propertyMappings,
      from,
      to,
      anchor,
    ),
  });
}

export function removedIncomingBlueprintMappingProperties(
  connection: ConnectionModel,
  nextTargetNode: NodeModel,
): string[] {
  const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
  return connection.propertyMappings
    .filter((mapping) =>
      !Object.prototype.hasOwnProperty.call(nextTargetNode.inputProperties, mapping.targetProperty) ||
      !Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty))
    .map((mapping) => mapping.targetProperty)
    .sort((left, right) => left.localeCompare(right));
}

export function removedOutgoingBlueprintMappingProperties(
  connection: ConnectionModel,
  nextSourceNode: NodeModel,
  nextAnchor: number,
): string[] {
  const sourceProperties = nextSourceNode.outputPropertiesForAnchor(nextAnchor);
  return connection.propertyMappings
    .filter((mapping) =>
      !Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty) ||
      !Object.prototype.hasOwnProperty.call(connection.to.inputProperties, mapping.targetProperty))
    .map((mapping) => mapping.sourceProperty)
    .sort((left, right) => left.localeCompare(right));
}

export function outputAnchorIndexByBlueprintChannel(node: NodeModel, channelName: string): number | null {
  const outputIndex = node.outputChannelNames.indexOf(channelName);
  if (outputIndex < 0) {
    return null;
  }

  return outputIndex + (node.inputChannelName ? 1 : 0);
}

export function collectionKindFromIncomingBlueprintMappings(
  nodeId: number,
  connections: readonly ConnectionModel[],
): TaskPropertyType | null {
  const candidates: TaskPropertyType[] = [];
  for (const connection of connections) {
    if (connection.to.id !== nodeId) {
      continue;
    }

    const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
    for (const mapping of connection.propertyMappings) {
      if (mapping.targetProperty !== 'items') {
        continue;
      }

      const sourceKind = sourceProperties[mapping.sourceProperty];
      if (sourceKind && isBlueprintCollectionKind(sourceKind)) {
        candidates.push(sourceKind);
      }
    }
  }

  return [...new Set(candidates)].length === 1 ? candidates[0] : null;
}

export function isBlueprintCollectionKind(kind: TaskPropertyType): kind is `${TaskPropertyBaseType}[]` {
  return kind.endsWith('[]');
}

export function toBlueprintScalarKind(kind: TaskPropertyType): TaskPropertyBaseType {
  return isBlueprintCollectionKind(kind)
    ? kind.slice(0, -2) as TaskPropertyBaseType
    : kind;
}
