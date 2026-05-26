import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { ConnectionModel } from './connection-model';
import {
  canCreateBlueprintConnection,
  createBlueprintConnectionModel,
  getDefaultBlueprintConnectionMappings,
  isDuplicateBlueprintConnection,
  reconcileBlueprintConnectionForNodeVersion,
} from './blueprint-editor-connection-policy';
import {
  createBlueprintCollectionIteratorNodeModel,
  createBlueprintPluginTaskNodeModel,
  moveBlueprintNodeModel,
  upsertBlueprintDataBridgeInputPropertyModel,
} from './blueprint-editor-node-factory';
import {
  restoreBlueprintGraphSnapshot,
  snapshotBlueprintGraph,
} from './blueprint-editor-graph-snapshot-utility';
import {
  selectBlueprintNodesWithinRect,
  toggleBlueprintNodeSelection,
} from './blueprint-editor-selection-utility';
import { NodeModel, TaskPropertyType } from './node-model';
import { ITaskPropertyDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';

function property(name: string, kind: ITaskPropertyDefinition['kind'] = 'String'): ITaskPropertyDefinition {
  return {
    name,
    kind,
    required: false,
    enumValues: [],
  };
}

function taskVersion(options: {
  id?: number;
  version?: string;
  inputProperties?: readonly ITaskPropertyDefinition[];
  outputChannels?: readonly { readonly name: string; readonly properties: readonly ITaskPropertyDefinition[] }[];
} = {}): ITaskVersion {
  return {
    id: options.id ?? 101,
    version: options.version ?? '1.0.0',
    state: 'Active',
    description: 'Version',
    inputChannel: options.inputProperties
      ? {
          name: 'Input',
          properties: [...options.inputProperties],
        }
      : null,
    outputChannels: [...options.outputChannels ?? []],
  };
}

function pluginNode(
  nodeId: number,
  options: {
    x?: number;
    y?: number;
    inputProperties?: readonly ITaskPropertyDefinition[];
    outputProperties?: readonly ITaskPropertyDefinition[];
  } = {},
): NodeModel {
  return createBlueprintPluginTaskNodeModel({
    nodeId,
    taskDefinitionId: nodeId * 10,
    taskName: `Task ${nodeId}`,
    coordinates: { x: options.x ?? 0, y: options.y ?? 0 },
    width: 320,
    renderStatus: null,
    failureMessage: null,
    version: taskVersion({
      inputProperties: options.inputProperties,
      outputChannels: options.outputProperties
        ? [{ name: 'Success', properties: options.outputProperties }]
        : [],
    }),
    designTimeInputOverrides: {},
  });
}

function dataBridgeNode(id: number, kind: 'input' | 'output'): NodeModel {
  return new NodeModel(
    id,
    id,
    0,
    0,
    id,
    '1.0.0',
    `Data Bridge ${kind}`,
    0,
    0,
    320,
    220,
    kind === 'output' ? 'Bridge' : null,
    ['Bridge'],
    false,
    null,
    null,
    kind === 'output' ? {} : { value: 'String' },
    { Bridge: {} },
    {},
    [],
    kind === 'output' ? 'Bridge' : null,
    ['Bridge'],
    {},
    true,
    'DataBridge',
    null,
    `data-bridge:${kind}:test`,
  );
}

describe('blueprint editor node factory', () => {
  it('creates task, Collection Iterator, moved, and Data Bridge input-property node models', () => {
    const task = pluginNode(1, {
      inputProperties: [property('town')],
      outputProperties: [property('result')],
    });
    const iterator = createBlueprintCollectionIteratorNodeModel({
      nodeId: 2,
      coordinates: { x: 420, y: 40 },
      itemKind: 'Guid',
    });
    const moved = moveBlueprintNodeModel(task, { x: 80, y: 120 });
    const bridge = upsertBlueprintDataBridgeInputPropertyModel(dataBridgeNode(3, 'output'), {
      name: 'result',
      kind: 'String',
    });

    expect(task.inputProperties).toEqual({ town: 'String' });
    expect(task.outputChannelProperties.Success).toEqual({ result: 'String' });
    expect(iterator.collectionIteratorEquation).toBe('Guid[] -> Guid');
    expect(moved.x).toBe(80);
    expect(moved.y).toBe(120);
    expect(bridge.inputProperties.result).toBe('String');
    expect(bridge.inputPropertyDefinitions.map((definition) => definition.name)).toContain('result');
  });
});

describe('blueprint editor connection policy', () => {
  it('creates connections, rejects duplicates, applies bridge policy, and reconciles version changes', () => {
    const source = pluginNode(1, { outputProperties: [property('result')] });
    const target = pluginNode(2, { inputProperties: [property('result')] });
    const connection = createBlueprintConnectionModel({
      source: { node: source, anchor: source.outputAnchors()[0]!.index },
      destination: { node: target, side: 'left', position: target.inputAnchorSnap()!.pos },
      propertyMappings: getDefaultBlueprintConnectionMappings(source, target, source.outputAnchors()[0]!.index),
    });
    const nextSource = createBlueprintPluginTaskNodeModel({
      nodeId: source.id,
      taskDefinitionId: source.taskDefinitionId,
      taskName: source.name,
      coordinates: { x: source.x, y: source.y },
      width: source.width,
      renderStatus: null,
      failureMessage: null,
      version: taskVersion({
        id: 202,
        outputChannels: [{ name: 'Done', properties: [property('result')] }],
      }),
      designTimeInputOverrides: {},
    });
    const nodeMap = new Map<number, NodeModel>([
      [nextSource.id, nextSource],
      [target.id, target],
    ]);

    expect(connection.propertyMappings).toEqual([{ sourceProperty: 'result', targetProperty: 'result' }]);
    expect(isDuplicateBlueprintConnection([connection], {
      source: { node: source, anchor: connection.anchor },
      destination: { node: target, side: connection.side, position: connection.pos },
    })).toBe(true);
    expect(canCreateBlueprintConnection(dataBridgeNode(4, 'input'), dataBridgeNode(5, 'output'))).toBe(false);
    expect(reconcileBlueprintConnectionForNodeVersion({
      connection,
      previousNode: source,
      nextNode: nextSource,
      nodeMap,
    })).toBeNull();
  });
});

describe('blueprint editor graph snapshot utility', () => {
  it('snapshots and restores graph nodes, connections, selection, and instance ids', () => {
    const source = pluginNode(1, { outputProperties: [property('result')] }).withInstanceId('source-instance');
    const target = pluginNode(2, { inputProperties: [property('result')] }).withInstanceId('target-instance');
    const connection = createBlueprintConnectionModel({
      source: { node: source, anchor: source.outputAnchors()[0]!.index },
      destination: { node: target, side: 'left', position: target.inputAnchorSnap()!.pos },
      id: 'connection-1',
      propertyMappings: [{ sourceProperty: 'result', targetProperty: 'result' }],
    });

    const snapshot = snapshotBlueprintGraph({
      nodes: [source, target],
      connections: [connection],
      selectedNodeIds: [source.id],
      selectedConnectionIds: [connection.id],
    });
    const restored = restoreBlueprintGraphSnapshot(snapshot);

    expect(restored.nodes.map((node) => node.instanceId)).toEqual(['source-instance', 'target-instance']);
    expect(restored.connections[0]?.from).toBe(restored.nodes[0]);
    expect(restored.connections[0]?.to).toBe(restored.nodes[1]);
    expect(restored.selectedNodeIds).toEqual([source.id]);
    expect(restored.selectedConnectionIds).toEqual([connection.id]);
  });
});

describe('blueprint editor selection utility', () => {
  it('selects marquee nodes and internal connections, including additive node toggles', () => {
    const source = pluginNode(1, { x: 0, outputProperties: [property('result' as string, 'String' as TaskPropertyType)] });
    const middle = pluginNode(2, { x: 420, inputProperties: [property('result')] });
    const outside = pluginNode(3, { x: 900, inputProperties: [property('result')] });
    const internalConnection = createBlueprintConnectionModel({
      source: { node: source, anchor: source.outputAnchors()[0]!.index },
      destination: { node: middle, side: 'left', position: middle.inputAnchorSnap()!.pos },
      id: 'internal',
    });
    const externalConnection = createBlueprintConnectionModel({
      source: { node: middle, anchor: middle.outputAnchors()[0]?.index ?? 0 },
      destination: { node: outside, side: 'left', position: outside.inputAnchorSnap()!.pos },
      id: 'external',
    });
    const marquee = selectBlueprintNodesWithinRect({
      nodes: [source, middle, outside],
      connections: [internalConnection, externalConnection],
      selectedNodeIds: [],
      bounds: { left: -20, top: -20, right: 760, bottom: 300 },
      additive: false,
    });
    const toggled = toggleBlueprintNodeSelection({
      selectedNodeIds: marquee.selectedNodeIds,
      connections: [internalConnection, externalConnection],
      nodeId: outside.id,
    });

    expect(marquee.selectedNodeIds).toEqual([source.id, middle.id]);
    expect(marquee.selectedConnectionIds).toEqual(['internal']);
    expect(toggled.selectedNodeIds).toEqual([source.id, middle.id, outside.id]);
    expect(toggled.selectedConnectionIds).toEqual(['internal', 'external']);
  });
});
