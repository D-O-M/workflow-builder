import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { BlueprintEditorStore } from './blueprint-editor.store';
import { ITaskDefinition, ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

function createTaskDefinition(
  id: number,
  name: string,
  options?: {
    inputChannelName?: string | null;
    outputChannelNames?: string[];
  },
): ITaskDefinition {
  return {
    id,
    name,
    versions: [
      {
        id: id * 100,
        version: '0.0.1',
        state: 'Active',
        description: `${name} description`,
        requiredGlobalState: null,
        inputChannel: options?.inputChannelName
          ? {
            id: id * 1000 + 1,
            name: options.inputChannelName,
            properties: {},
          }
          : null,
        outputChannels: (options?.outputChannelNames ?? []).map((channelName, index) => ({
          id: id * 1000 + 10 + index,
          name: channelName,
          properties: {},
        })),
      },
    ],
  };
}

function property(name: string, kind: ITaskPropertyDefinition['kind'] = 'String'): ITaskPropertyDefinition {
  return {
    name,
    kind,
    required: false,
    enumValues: [],
  };
}

function createVersionedTaskDefinition(): ITaskDefinition {
  return {
    id: 40,
    name: 'Versioned Task',
    versions: [
      {
        id: 401,
        version: '1.0.0',
        state: 'Active',
        description: 'Old version',
        requiredGlobalState: null,
        inputChannel: {
          id: 4001,
          name: 'Input',
          properties: [property('legacyInput')],
        },
        outputChannels: [
          {
            id: 4010,
            name: 'Success',
            properties: [property('legacyOutput')],
          },
        ],
      },
      {
        id: 402,
        version: '1.0.2',
        state: 'Active',
        description: 'Patch version',
        requiredGlobalState: null,
        inputChannel: {
          id: 4002,
          name: 'Input',
          properties: [property('legacyInput'), property('patchInput')],
        },
        outputChannels: [
          {
            id: 4011,
            name: 'Success',
            properties: [property('legacyOutput'), property('patchOutput')],
          },
        ],
      },
      {
        id: 403,
        version: '2.0.0',
        state: 'Active',
        description: 'Major version',
        requiredGlobalState: null,
        inputChannel: {
          id: 4003,
          name: 'Input',
          properties: [property('nextInput')],
        },
        outputChannels: [
          {
            id: 4012,
            name: 'Done',
            properties: [property('nextOutput')],
          },
        ],
      },
    ],
  };
}

function createMappingTargetTaskDefinition(): ITaskDefinition {
  return {
    id: 41,
    name: 'Target Task',
    versions: [
      {
        id: 410,
        version: '1.0.0',
        state: 'Active',
        description: 'Target',
        requiredGlobalState: null,
        inputChannel: {
          id: 4101,
          name: 'Input',
          properties: [property('legacyOutput')],
        },
        outputChannels: [],
      },
    ],
  };
}

describe('BlueprintEditorStore', () => {
  it('creates new nodes on the latest available semantic version', () => {
    const store = new BlueprintEditorStore();
    const node = store.createNode(createVersionedTaskDefinition(), { x: 0, y: 0 });

    expect(node.resolvedVersion).toBe('2.0.0');
    expect(node.allowedMajor).toBe(2);
    expect(node.allowedMinor).toBe(0);
  });

  it('preserves compatible mappings and static overrides when changing patch versions', () => {
    const store = new BlueprintEditorStore();
    const versionedTask = createVersionedTaskDefinition();
    const source = store.createNode(versionedTask, { x: 0, y: 0 });
    store.updateNodeTaskVersion(source.id, versionedTask.versions[0]!);
    store.updateNodeDesignTimeOverrides(source.id, { legacyInput: 'fixed' });
    const oldSource = store.nodes().find((node) => node.id === source.id)!;
    const target = store.createNode(createMappingTargetTaskDefinition(), { x: 420, y: 0 });
    const connection = store.createConnection(oldSource.id, target.id, oldSource.outputAnchors()[0]!.index, 'left', target.inputAnchorSnap()!.pos)!;
    store.updateConnectionPropertyMappings(connection.id, [{ sourceProperty: 'legacyOutput', targetProperty: 'legacyOutput' }]);

    const impact = store.analyzeNodeVersionChange(oldSource.id, versionedTask.versions[1]!);
    store.updateNodeTaskVersion(oldSource.id, versionedTask.versions[1]!);

    const updatedSource = store.nodes().find((node) => node.id === oldSource.id)!;
    expect(impact.hasDestructiveChanges).toBe(false);
    expect(updatedSource.resolvedVersion).toBe('1.0.2');
    expect(updatedSource.designTimeInputOverrides).toEqual({ legacyInput: 'fixed' });
    expect(store.connections()).toHaveLength(1);
    expect(store.connections()[0]!.propertyMappings).toEqual([
      { sourceProperty: 'legacyOutput', targetProperty: 'legacyOutput' },
    ]);
  });

  it('reports and prunes incompatible mappings and static overrides when changing versions', () => {
    const store = new BlueprintEditorStore();
    const versionedTask = createVersionedTaskDefinition();
    const source = store.createNode(versionedTask, { x: 0, y: 0 });
    store.updateNodeTaskVersion(source.id, versionedTask.versions[0]!);
    store.updateNodeDesignTimeOverrides(source.id, { legacyInput: 'fixed' });
    const oldSource = store.nodes().find((node) => node.id === source.id)!;
    const target = store.createNode(createMappingTargetTaskDefinition(), { x: 420, y: 0 });
    store.createConnection(oldSource.id, target.id, oldSource.outputAnchors()[0]!.index, 'left', target.inputAnchorSnap()!.pos);

    const impact = store.analyzeNodeVersionChange(oldSource.id, versionedTask.versions[2]!);
    store.updateNodeTaskVersion(oldSource.id, versionedTask.versions[2]!);

    const updatedSource = store.nodes().find((node) => node.id === oldSource.id)!;
    expect(impact.hasDestructiveChanges).toBe(true);
    expect(impact.removedOverrideProperties).toEqual(['legacyInput']);
    expect(impact.removedConnections).toHaveLength(1);
    expect(updatedSource.designTimeInputOverrides).toEqual({});
    expect(store.connections()).toEqual([]);
  });

  it('selects enclosed nodes and auto-selects only internal connections', () => {
    const store = new BlueprintEditorStore();
    const sourceTask = createTaskDefinition(1, 'Lookup', { outputChannelNames: ['Success'] });
    const middleTask = createTaskDefinition(2, 'Forecast', {
      inputChannelName: 'Input',
      outputChannelNames: ['Success'],
    });
    const outsideTask = createTaskDefinition(3, 'Notify', { inputChannelName: 'Input' });

    const source = store.createNode(sourceTask, { x: 0, y: 0 });
    const middle = store.createNode(middleTask, { x: 400, y: 0 });
    const outside = store.createNode(outsideTask, { x: 920, y: 0 });

    store.createConnection(source.id, middle.id, 0, 'left', middle.inputAnchorSnap()!.pos);
    store.createConnection(middle.id, outside.id, 1, 'left', outside.inputAnchorSnap()!.pos);

    store.selectNodesWithinRect(-20, -20, 760, 280, false);

    expect(store.selectedNodeIds()).toEqual([source.id, middle.id]);
    expect(store.selectedConnectionIds()).toHaveLength(1);
    expect(store.connections().find((connection) => connection.id === store.selectedConnectionIds()[0])?.from.id)
      .toBe(source.id);
    expect(store.connections().find((connection) => connection.id === store.selectedConnectionIds()[0])?.to.id)
      .toBe(middle.id);
  });

  it('copies and pastes a selected subgraph with preserved internal wiring', () => {
    const store = new BlueprintEditorStore();
    const sourceTask = createTaskDefinition(1, 'Lookup', { outputChannelNames: ['Success'] });
    const destinationTask = createTaskDefinition(2, 'Forecast', {
      inputChannelName: 'Input',
      outputChannelNames: ['Success'],
    });

    const source = store.createNode(sourceTask, { x: 20, y: 40 });
    const destination = store.createNode(destinationTask, { x: 440, y: 40 });
    store.createConnection(source.id, destination.id, 0, 'left', destination.inputAnchorSnap()!.pos);

    store.selectOnlyNode(source.id);
    store.toggleNodeSelection(destination.id);
    store.copySelection();
    store.pasteClipboard();

    expect(store.nodes()).toHaveLength(4);
    expect(store.connections()).toHaveLength(2);
    expect(store.selectedNodeIds()).toHaveLength(2);
    expect(store.selectedConnectionIds()).toHaveLength(1);

    const pastedNodes = store.nodes().filter((node) => store.selectedNodeIds().includes(node.id));
    expect(pastedNodes.map((node) => ({ x: node.x, y: node.y }))).toEqual([
      { x: 60, y: 80 },
      { x: 480, y: 80 },
    ]);

    const pastedConnection = store.connections().find((connection) =>
      store.selectedConnectionIds().includes(connection.id),
    );
    expect(pastedConnection).toBeDefined();
    expect(store.selectedNodeIds()).toContain(pastedConnection!.from.id);
    expect(store.selectedNodeIds()).toContain(pastedConnection!.to.id);
  });

  it('restores deleted nodes and connections on undo', () => {
    const store = new BlueprintEditorStore();
    const sourceTask = createTaskDefinition(1, 'Lookup', { outputChannelNames: ['Success'] });
    const destinationTask = createTaskDefinition(2, 'Forecast', {
      inputChannelName: 'Input',
      outputChannelNames: ['Success'],
    });

    const source = store.createNode(sourceTask, { x: 0, y: 0 });
    const destination = store.createNode(destinationTask, { x: 400, y: 0 });
    store.createConnection(source.id, destination.id, 0, 'left', destination.inputAnchorSnap()!.pos);

    store.selectOnlyNode(source.id);
    store.toggleNodeSelection(destination.id);
    store.deleteSelection();

    expect(store.nodes()).toHaveLength(0);
    expect(store.connections()).toHaveLength(0);

    store.undo();

    expect(store.nodes()).toHaveLength(2);
    expect(store.connections()).toHaveLength(1);
    expect(store.nodes().map((node) => node.name)).toEqual(['Lookup', 'Forecast']);
  });

  it('restores the undone graph state on redo', () => {
    const store = new BlueprintEditorStore();
    const sourceTask = createTaskDefinition(1, 'Lookup', { outputChannelNames: ['Success'] });
    const destinationTask = createTaskDefinition(2, 'Forecast', {
      inputChannelName: 'Input',
      outputChannelNames: ['Success'],
    });

    const source = store.createNode(sourceTask, { x: 0, y: 0 });
    const destination = store.createNode(destinationTask, { x: 400, y: 0 });
    store.createConnection(source.id, destination.id, 0, 'left', destination.inputAnchorSnap()!.pos);

    store.selectOnlyNode(source.id);
    store.toggleNodeSelection(destination.id);
    store.deleteSelection();
    store.undo();
    store.redo();

    expect(store.nodes()).toHaveLength(0);
    expect(store.connections()).toHaveLength(0);
    expect(store.canRedo()).toBe(false);
    expect(store.canUndo()).toBe(true);
  });

  it('rejects connections from a node output back into its own input', () => {
    const store = new BlueprintEditorStore();
    const task = createTaskDefinition(1, 'Transform', {
      inputChannelName: 'Input',
      outputChannelNames: ['Success'],
    });
    const node = store.createNode(task, { x: 0, y: 0 });

    const connection = store.createConnection(
      node.id,
      node.id,
      node.outputAnchors()[0]!.index,
      'left',
      node.inputAnchorSnap()!.pos,
    );

    expect(connection).toBeNull();
    expect(store.connections()).toEqual([]);
  });

  it('bumps the graph revision when replacing the whole graph state', () => {
    const store = new BlueprintEditorStore();
    const task = createTaskDefinition(1, 'Lookup', { outputChannelNames: ['Success'] });

    expect(store.graphRevision()).toBe(0);

    const node = store.createNode(task, { x: 0, y: 0 });
    store.clearGraph();

    expect(store.graphRevision()).toBe(1);

    store.loadGraph([node], []);

    expect(store.graphRevision()).toBe(2);
  });
});
