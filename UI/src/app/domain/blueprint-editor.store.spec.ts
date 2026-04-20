import { describe, expect, it } from 'vitest';
import { BlueprintEditorStore } from './blueprint-editor.store';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

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

describe('BlueprintEditorStore', () => {
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
});
