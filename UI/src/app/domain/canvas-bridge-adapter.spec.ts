import { describe, expect, it } from 'vitest';
import { RenderedNode } from './canvas-render-model';
import { NodePortDefinition } from './node-model';
import { createCanvasInputPropertyHit, createCanvasOutputPropertyDragEvent } from './canvas-bridge-adapter';

function renderedNode(): RenderedNode {
  return {
    id: 12,
    name: 'Lookup',
    base: {
      instanceId: 'lookup-node',
      inputProperties: {
        town: 'String',
      },
      outputPropertiesForAnchor: () => ({
        result: 'Guid',
      }),
      outputChannelNameForAnchor: () => 'Success',
    },
  } as unknown as RenderedNode;
}

function port(kind: NodePortDefinition['kind'], propertyName: string | null): NodePortDefinition {
  return {
    key: `${kind}:${propertyName ?? 'summary'}`,
    kind,
    anchorIndex: kind.startsWith('input') ? null : 0,
    channelName: kind.startsWith('input') ? 'Input' : 'Success',
    channelLabel: null,
    propertyName,
    label: propertyName ?? 'Summary',
    side: kind.startsWith('input') ? 'left' : 'right',
    x: 0,
    y: 0,
    radius: 7,
  };
}

describe('canvas bridge adapter', () => {
  it('creates source property drag events from rendered node state', () => {
    expect(createCanvasOutputPropertyDragEvent(
      renderedNode(),
      { from: 12, anchor: 0, sourceProperty: 'result' },
      240,
      180,
      false,
    )).toEqual({
      nodeId: 12,
      nodeInstanceId: 'lookup-node',
      nodeName: 'Lookup',
      channelName: 'Success',
      propertyName: 'result',
      propertyKind: 'Guid',
      clientX: 240,
      clientY: 180,
      cancelled: false,
    });
  });

  it('rejects non-property bridge drag sources', () => {
    expect(createCanvasOutputPropertyDragEvent(
      renderedNode(),
      { from: 12, anchor: 0, sourceProperty: null },
      240,
      180,
      false,
    )).toBeNull();
  });

  it('creates input hits for property ports only', () => {
    expect(createCanvasInputPropertyHit(renderedNode(), port('input-property', 'town'), 320, 210)).toMatchObject({
      nodeId: 12,
      nodeInstanceId: 'lookup-node',
      nodeName: 'Lookup',
      propertyName: 'town',
      propertyKind: 'String',
      clientX: 320,
      clientY: 210,
    });
    expect(createCanvasInputPropertyHit(renderedNode(), port('input-summary', null), 320, 210)).toBeNull();
  });
});
