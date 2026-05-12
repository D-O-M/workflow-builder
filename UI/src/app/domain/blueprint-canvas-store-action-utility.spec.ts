import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { BlueprintCanvasEvent } from '../components/blueprint-canvas-contract';
import { BlueprintEditorStore } from './blueprint-editor.store';
import { applyBlueprintCanvasStoreEvent } from './blueprint-canvas-store-action-utility';
import { NodeModel } from './node-model';

function taskNode(id: number, name: string, x: number): NodeModel {
  return new NodeModel(
    id,
    id,
    0,
    0,
    id,
    '1.0.0',
    name,
    x,
    0,
    320,
    220,
    'Input',
    ['Success'],
    false,
    null,
    null,
    { value: 'String' },
    { Success: { result: 'String' } },
  );
}

function dataBridgeOutputNode(id: number): NodeModel {
  return new NodeModel(
    id,
    id,
    0,
    0,
    id,
    '1.0.0',
    'Data Bridge Output',
    760,
    0,
    320,
    220,
    'Bridge',
    ['Bridge'],
    false,
    null,
    null,
    {},
    { Bridge: {} },
    {},
    [],
    'Bridge',
    ['Bridge'],
    {},
    true,
    'DataBridge',
    null,
    'data-bridge:output:test',
  );
}

describe('applyBlueprintCanvasStoreEvent', () => {
  it('applies focus, selection, history, and node position events', () => {
    const store = new BlueprintEditorStore();
    const source = taskNode(1, 'Source', 0);
    const target = taskNode(2, 'Target', 420);
    store.loadGraph({ nodes: [source, target], connections: [] });

    applyBlueprintCanvasStoreEvent(store, { kind: 'focusChanged', focused: true });
    applyBlueprintCanvasStoreEvent(store, { kind: 'selectOnlyNode', nodeId: source.id });
    applyBlueprintCanvasStoreEvent(store, { kind: 'captureHistory' });
    applyBlueprintCanvasStoreEvent(store, {
      kind: 'updateNodePositions',
      positions: [{ nodeId: source.id, coordinates: { x: 80, y: 40 } }],
    });

    expect(store.canvasFocused()).toBe(true);
    expect(store.selectedNodeIds()).toEqual([source.id]);
    expect(store.nodes().find((node) => node.id === source.id)?.x).toBe(80);
  });

  it('creates normal connections with property mappings', () => {
    const store = new BlueprintEditorStore();
    const source = taskNode(1, 'Source', 0);
    const target = taskNode(2, 'Target', 420);
    store.loadGraph({ nodes: [source, target], connections: [] });

    const event: BlueprintCanvasEvent = {
      kind: 'createConnection',
      request: {
        from: source.id,
        to: target.id,
        anchor: source.outputAnchors()[0]!.index,
        side: 'left',
        position: target.inputAnchorSnap()!.pos,
        propertyMapping: {
          sourceProperty: 'result',
          targetProperty: 'value',
        },
      },
    };

    const connection = applyBlueprintCanvasStoreEvent(store, event);

    expect(connection?.propertyMappings).toEqual([{ sourceProperty: 'result', targetProperty: 'value' }]);
    expect(store.connections()).toHaveLength(1);
  });

  it('creates Data Bridge output connections after upserting the bridge input property', () => {
    const store = new BlueprintEditorStore();
    const source = taskNode(1, 'Source', 0);
    const bridgeOutput = dataBridgeOutputNode(2);
    store.loadGraph({ nodes: [source, bridgeOutput], connections: [] });

    const event: BlueprintCanvasEvent = {
      kind: 'createConnection',
      request: {
        from: source.id,
        to: bridgeOutput.id,
        anchor: source.outputAnchors()[0]!.index,
        side: 'left',
        position: bridgeOutput.inputAnchorSnap()!.pos,
        upsertDataBridgeInputProperty: {
          nodeId: bridgeOutput.id,
          propertyName: 'result',
          propertyKind: 'String',
        },
        propertyMapping: {
          sourceProperty: 'result',
          targetProperty: 'result',
        },
      },
    };

    const connection = applyBlueprintCanvasStoreEvent(store, event);
    const updatedBridge = store.nodes().find((node) => node.id === bridgeOutput.id)!;

    expect(updatedBridge.inputProperties.result).toBe('String');
    expect(connection?.propertyMappings).toEqual([{ sourceProperty: 'result', targetProperty: 'result' }]);
  });
});
