import { NodeKind, NodeModel, TaskPropertyBaseType, TaskPropertyType } from './node-model';
import { compareTaskVersions, getTaskVersionLane } from './task-version-utils';
import type { ITaskDefinition, ITaskPropertyDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';

export interface ProcessCanvasDetail {
  readonly nodes: readonly ProcessCanvasNode[];
}

export interface ProcessCanvasNode {
  readonly taskDefinitionId: number;
  readonly nodeKind?: NodeKind | null;
  readonly collectionIteratorItemKind?: TaskPropertyBaseType | null;
  readonly allowedMajor: number;
  readonly allowedMinor: number;
  readonly resolvedVersionId: number;
  readonly resolvedVersion?: string | null;
  readonly name: string;
  readonly status: 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  readonly position: {
    readonly x: number;
    readonly y: number;
  };
  readonly failureMessage?: string | null;
  readonly inputChannelName?: string | null;
  readonly outputChannelNames: readonly string[];
}

export function buildProcessCanvasNodes(
  detail: ProcessCanvasDetail,
  taskDefinitions: readonly ITaskDefinition[],
): NodeModel[] {
  const taskDefinitionById = new Map(
    taskDefinitions.map((taskDefinition) => [taskDefinition.id, taskDefinition] as const),
  );

  return detail.nodes.map((node, index) => {
    if (node.nodeKind === 'CollectionIterator') {
      const itemKind = node.collectionIteratorItemKind ?? 'String';
      const collectionKind: TaskPropertyType = `${itemKind}[]`;
      return new NodeModel(
        index + 1,
        0,
        0,
        0,
        0,
        'Native',
        'Collection Iterator',
        node.position.x,
        node.position.y,
        NodeModel.collectionIteratorOperatorWidth,
        NodeModel.collectionIteratorOperatorHeight,
        'Collection',
        ['Item'],
        false,
        node.status,
        node.failureMessage ?? null,
        { items: collectionKind },
        { Item: { item: itemKind } },
        {},
        [{ name: 'items', kind: collectionKind, required: true, enumValues: [] }],
        'Collection',
        ['Item'],
        { Item: [{ name: 'item', kind: itemKind, required: true, enumValues: [] }] },
        false,
        'CollectionIterator',
        itemKind,
      );
    }

    const taskDefinition = taskDefinitionById.get(node.taskDefinitionId);
    const taskVersion = taskDefinition
      ? resolveVersionForLane(taskDefinition, node.allowedMajor, node.allowedMinor, node.resolvedVersionId)
      : null;
    const inputPropertyDefinitions = clonePropertyDefinitions(taskVersion?.inputChannel?.properties ?? []);
    const outputChannelPropertyDefinitions = Object.fromEntries(
      (taskVersion?.outputChannels ?? []).map((channel) => [
        channel.name,
        clonePropertyDefinitions(channel.properties),
      ]),
    );
    const outputChannelNames = [...(taskVersion?.outputChannels.map((channel) => channel.name) ?? node.outputChannelNames)];

    return new NodeModel(
      index + 1,
      node.taskDefinitionId,
      node.allowedMajor,
      node.allowedMinor,
      taskVersion?.id ?? node.resolvedVersionId,
      taskVersion?.version ?? node.resolvedVersion ?? null,
      taskDefinition ? taskDefinitionDisplayName(taskDefinition) : node.name,
      node.position.x,
      node.position.y,
      320,
      Math.max(220, 168 + Math.max(outputChannelNames.length, 1) * 34),
      taskVersion?.inputChannel?.name ?? node.inputChannelName ?? null,
      outputChannelNames,
      false,
      node.status,
      node.failureMessage ?? null,
      propertyKindMap(inputPropertyDefinitions),
      Object.fromEntries(
        Object.entries(outputChannelPropertyDefinitions).map(([channelName, properties]) => [
          channelName,
          propertyKindMap(properties),
        ]),
      ),
      {},
      inputPropertyDefinitions,
      taskVersion?.inputChannel?.displayName ?? null,
      (taskVersion?.outputChannels ?? []).map((channel) => channel.displayName?.trim() || channel.name),
      outputChannelPropertyDefinitions,
      false,
      'PluginTask',
      null,
    );
  });
}

function resolveVersionForLane(
  taskDefinition: ITaskDefinition,
  allowedMajor: number,
  allowedMinor: number,
  preferredVersionId?: number | null,
): ITaskVersion | null {
  if (preferredVersionId) {
    const preferredVersion = taskDefinition.versions.find((version) => version.id === preferredVersionId);
    if (preferredVersion) {
      return preferredVersion;
    }
  }

  const laneVersions = taskDefinition.versions.filter((version) => {
    const [major, minor] = getTaskVersionLane(version.version);
    return major === allowedMajor && minor === allowedMinor;
  });

  return [...laneVersions].sort((left, right) => compareTaskVersions(right, left))[0] ?? null;
}

function taskDefinitionDisplayName(taskDefinition: ITaskDefinition): string {
  const displayName = taskDefinition.displayName?.trim();
  return displayName && displayName.length > 0
    ? displayName
    : taskDefinition.name;
}

function clonePropertyDefinitions(properties: readonly ITaskPropertyDefinition[]): ITaskPropertyDefinition[] {
  return properties.map((property) => ({
    ...property,
    enumValues: [...property.enumValues],
  }));
}

function propertyKindMap(
  properties: readonly ITaskPropertyDefinition[],
): Record<string, ITaskPropertyDefinition['kind']> {
  return Object.fromEntries(properties.map((property) => [property.name, property.kind]));
}
