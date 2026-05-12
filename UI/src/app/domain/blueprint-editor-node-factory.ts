import { ITaskPropertyDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';
import { NodeKind, NodeModel, NodeRenderStatus, Point, TaskPropertyBaseType, TaskPropertyType } from './node-model';
import { normalizeTaskPropertyDefinitions, taskPropertyKindMap } from './task-property-form-utils';
import { getTaskVersionLane } from './task-version-utils';

export interface BlueprintPluginTaskNodeModelRequest {
  readonly nodeId: number;
  readonly taskDefinitionId: number;
  readonly taskName: string;
  readonly coordinates: Point;
  readonly width: number;
  readonly renderStatus: NodeRenderStatus | null;
  readonly failureMessage: string | null;
  readonly version: ITaskVersion;
  readonly designTimeInputOverrides: Record<string, unknown>;
}

export interface BlueprintCollectionIteratorNodeModelRequest {
  readonly nodeId: number;
  readonly coordinates: Point;
  readonly itemKind: TaskPropertyBaseType | null;
  readonly instanceId?: string | null;
  readonly expanded?: boolean;
}

export interface BlueprintNodeSnapshotModel {
  readonly id: number;
  readonly nodeKind: NodeKind;
  readonly collectionIteratorItemKind: TaskPropertyBaseType | null;
  readonly taskDefinitionId: number;
  readonly allowedMajor: number;
  readonly allowedMinor: number;
  readonly resolvedVersionId: number;
  readonly resolvedVersion: string | null;
  readonly name: string;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly inputChannelName: string | null;
  readonly outputChannelNames: readonly string[];
  readonly hasUpgradeWarning: boolean;
  readonly renderStatus: NodeRenderStatus | null;
  readonly failureMessage: string | null;
  readonly inputProperties: NodeModel['inputProperties'];
  readonly inputPropertyDefinitions: readonly ITaskPropertyDefinition[];
  readonly outputChannelProperties: NodeModel['outputChannelProperties'];
  readonly designTimeInputOverrides: NodeModel['designTimeInputOverrides'];
  readonly inputChannelDisplayName: string | null;
  readonly outputChannelDisplayNames: readonly string[];
  readonly outputChannelPropertyDefinitions: Record<string, readonly ITaskPropertyDefinition[]>;
  readonly instanceId: string | null;
}

export function cloneBlueprintPropertyDefinitions(
  properties: readonly ITaskPropertyDefinition[] | Record<string, TaskPropertyType>,
): ITaskPropertyDefinition[] {
  return normalizeTaskPropertyDefinitions(properties);
}

export function cloneBlueprintOutputChannelPropertyDefinitions(
  outputChannels: readonly { readonly name: string; readonly properties: readonly ITaskPropertyDefinition[] }[] |
    Record<string, readonly ITaskPropertyDefinition[]>,
): Record<string, ITaskPropertyDefinition[]> {
  if (Array.isArray(outputChannels)) {
    return Object.fromEntries(outputChannels.map((channel) => [
      channel.name,
      cloneBlueprintPropertyDefinitions(channel.properties),
    ]));
  }

  return Object.fromEntries(
    Object.entries(outputChannels).map(([channelName, properties]) => [
      channelName,
      cloneBlueprintPropertyDefinitions(properties),
    ]),
  );
}

export function createBlueprintPluginTaskNodeModel(request: BlueprintPluginTaskNodeModelRequest): NodeModel {
  const [allowedMajor, allowedMinor] = getTaskVersionLane(request.version.version);
  const inputPropertyDefinitions = cloneBlueprintPropertyDefinitions(request.version.inputChannel?.properties ?? []);
  const outputChannelPropertyDefinitions = cloneBlueprintOutputChannelPropertyDefinitions(request.version.outputChannels);

  return new NodeModel(
    request.nodeId,
    request.taskDefinitionId,
    allowedMajor,
    allowedMinor,
    request.version.id,
    request.version.version,
    request.taskName,
    request.coordinates.x,
    request.coordinates.y,
    request.width,
    Math.max(220, 168 + Math.max(request.version.outputChannels.length, 1) * 34),
    request.version.inputChannel?.name ?? null,
    request.version.outputChannels.map((channel) => channel.name),
    false,
    request.renderStatus,
    request.failureMessage,
    taskPropertyKindMap(inputPropertyDefinitions),
    Object.fromEntries(request.version.outputChannels.map((channel) => [
      channel.name,
      taskPropertyKindMap(channel.properties),
    ])),
    { ...request.designTimeInputOverrides },
    inputPropertyDefinitions,
    request.version.inputChannel?.displayName ?? null,
    request.version.outputChannels.map((channel) => channel.displayName?.trim() || channel.name),
    outputChannelPropertyDefinitions,
    false,
    'PluginTask',
    null,
  );
}

export function createBlueprintCollectionIteratorNodeModel(request: BlueprintCollectionIteratorNodeModelRequest): NodeModel {
  const concreteItemKind = request.itemKind ?? 'String';
  const inputProperties: Record<string, TaskPropertyType> = {
    items: `${concreteItemKind}[]`,
  };
  const outputChannelProperties: Record<string, Record<string, TaskPropertyType>> = {
    Item: {
      item: concreteItemKind,
    },
  };
  const inputPropertyDefinitions: ITaskPropertyDefinition[] = [{
    name: 'items',
    kind: `${concreteItemKind}[]`,
    required: true,
    enumValues: [],
  }];
  const outputChannelPropertyDefinitions: Record<string, ITaskPropertyDefinition[]> = {
    Item: [{
      name: 'item',
      kind: concreteItemKind,
      required: true,
      enumValues: [],
    }],
  };

  const node = new NodeModel(
    request.nodeId,
    0,
    0,
    0,
    0,
    'Native',
    'Collection Iterator',
    request.coordinates.x,
    request.coordinates.y,
    NodeModel.collectionIteratorOperatorWidth,
    NodeModel.collectionIteratorOperatorHeight,
    'Collection',
    ['Item'],
    false,
    null,
    null,
    inputProperties,
    outputChannelProperties,
    {},
    inputPropertyDefinitions,
    'Collection',
    ['Item'],
    outputChannelPropertyDefinitions,
    false,
    'CollectionIterator',
    request.itemKind,
    request.instanceId ?? null,
  );

  return request.expanded ? node.withExpanded(true) : node;
}

export function moveBlueprintNodeModel(node: NodeModel, coordinates: Point): NodeModel {
  return recreateBlueprintNodeModel(node, {
    x: coordinates.x,
    y: coordinates.y,
  });
}

export function updateBlueprintNodeDesignTimeOverridesModel(
  node: NodeModel,
  overrides: Record<string, unknown>,
): NodeModel {
  return recreateBlueprintNodeModel(node, {
    designTimeInputOverrides: { ...overrides },
  });
}

export function upsertBlueprintDataBridgeInputPropertyModel(
  node: NodeModel,
  property: { readonly name: string; readonly kind: TaskPropertyType },
): NodeModel {
  const inputProperties = {
    ...node.inputProperties,
    [property.name]: property.kind,
  };
  const inputPropertyDefinitions = [
    ...cloneBlueprintPropertyDefinitions(node.inputPropertyDefinitions),
    {
      name: property.name,
      kind: property.kind,
      required: false,
      enumValues: [],
    },
  ];

  return recreateBlueprintNodeModel(node, {
    inputProperties,
    inputPropertyDefinitions,
  });
}

export function instantiateBlueprintNodeSnapshot(snapshot: BlueprintNodeSnapshotModel): NodeModel {
  return new NodeModel(
    snapshot.id,
    snapshot.taskDefinitionId,
    snapshot.allowedMajor,
    snapshot.allowedMinor,
    snapshot.resolvedVersionId,
    snapshot.resolvedVersion,
    snapshot.name,
    snapshot.x,
    snapshot.y,
    snapshot.width,
    snapshot.height,
    snapshot.inputChannelName,
    [...snapshot.outputChannelNames],
    snapshot.hasUpgradeWarning,
    snapshot.renderStatus,
    snapshot.failureMessage,
    { ...snapshot.inputProperties },
    cloneOutputChannelProperties(snapshot.outputChannelProperties),
    { ...snapshot.designTimeInputOverrides },
    cloneBlueprintPropertyDefinitions(snapshot.inputPropertyDefinitions),
    snapshot.inputChannelDisplayName,
    [...snapshot.outputChannelDisplayNames],
    cloneBlueprintOutputChannelPropertyDefinitions(snapshot.outputChannelPropertyDefinitions),
    false,
    snapshot.nodeKind,
    snapshot.collectionIteratorItemKind,
    snapshot.instanceId,
  );
}

export function snapshotBlueprintNodeModel(node: NodeModel): BlueprintNodeSnapshotModel {
  return {
    id: node.id,
    nodeKind: node.nodeKind,
    collectionIteratorItemKind: node.collectionIteratorItemKind,
    taskDefinitionId: node.taskDefinitionId,
    allowedMajor: node.allowedMajor,
    allowedMinor: node.allowedMinor,
    resolvedVersionId: node.resolvedVersionId,
    resolvedVersion: node.resolvedVersion,
    name: node.name,
    x: node.x,
    y: node.y,
    width: node.width,
    height: node.height,
    inputChannelName: node.inputChannelName,
    outputChannelNames: [...node.outputChannelNames],
    hasUpgradeWarning: node.hasUpgradeWarning,
    renderStatus: node.renderStatus,
    failureMessage: node.failureMessage,
    inputProperties: { ...node.inputProperties },
    inputPropertyDefinitions: cloneBlueprintPropertyDefinitions(node.inputPropertyDefinitions),
    outputChannelProperties: cloneOutputChannelProperties(node.outputChannelProperties),
    designTimeInputOverrides: { ...node.designTimeInputOverrides },
    inputChannelDisplayName: node.inputChannelDisplayName,
    outputChannelDisplayNames: [...node.outputChannelDisplayNames],
    outputChannelPropertyDefinitions: cloneBlueprintOutputChannelPropertyDefinitions(node.outputChannelPropertyDefinitions),
    instanceId: node.instanceId,
  };
}

function recreateBlueprintNodeModel(
  node: NodeModel,
  overrides: Partial<{
    readonly x: number;
    readonly y: number;
    readonly inputProperties: NodeModel['inputProperties'];
    readonly designTimeInputOverrides: NodeModel['designTimeInputOverrides'];
    readonly inputPropertyDefinitions: readonly ITaskPropertyDefinition[];
  }>,
): NodeModel {
  return new NodeModel(
    node.id,
    node.taskDefinitionId,
    node.allowedMajor,
    node.allowedMinor,
    node.resolvedVersionId,
    node.resolvedVersion,
    node.name,
    overrides.x ?? node.x,
    overrides.y ?? node.y,
    node.width,
    node.height,
    node.inputChannelName,
    [...node.outputChannelNames],
    node.hasUpgradeWarning,
    node.renderStatus,
    node.failureMessage,
    overrides.inputProperties ? { ...overrides.inputProperties } : { ...node.inputProperties },
    cloneOutputChannelProperties(node.outputChannelProperties),
    overrides.designTimeInputOverrides
      ? { ...overrides.designTimeInputOverrides }
      : { ...node.designTimeInputOverrides },
    overrides.inputPropertyDefinitions
      ? cloneBlueprintPropertyDefinitions(overrides.inputPropertyDefinitions)
      : cloneBlueprintPropertyDefinitions(node.inputPropertyDefinitions),
    node.inputChannelDisplayName,
    [...node.outputChannelDisplayNames],
    cloneBlueprintOutputChannelPropertyDefinitions(node.outputChannelPropertyDefinitions),
    node.expanded,
    node.nodeKind,
    node.collectionIteratorItemKind,
    node.instanceId,
  );
}

function cloneOutputChannelProperties(
  propertiesByChannel: NodeModel['outputChannelProperties'],
): NodeModel['outputChannelProperties'] {
  return Object.fromEntries(
    Object.entries(propertiesByChannel).map(([channelName, properties]) => [channelName, { ...properties }]),
  );
}
