import { describe, expect, it } from 'vitest';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import { CanvasNodeLayoutCache } from './canvas-node-layout-cache';
import {
  CANVAS_NODE_LAYOUT_TOKENS,
  CanvasNodeLayoutFactory,
  CanvasNodeLayoutRequest,
} from './canvas-node-layout-factory';
import { NodeModel } from './node-model';

function property(name: string, kind: ITaskPropertyDefinition['kind'] = 'String'): ITaskPropertyDefinition {
  return {
    name,
    kind,
    required: false,
    enumValues: [],
  };
}

function layoutRequest(overrides: Partial<CanvasNodeLayoutRequest> = {}): CanvasNodeLayoutRequest {
  return {
    x: 100,
    y: 80,
    width: 320,
    inputChannelName: 'Input',
    outputChannelNames: ['Success'],
    inputProperties: { town: 'String' },
    outputChannelProperties: { Success: { town: 'String', score: 'Integer' } },
    inputPropertyDefinitions: [property('town')],
    inputChannelDisplayName: null,
    outputChannelDisplayNames: [],
    outputChannelPropertyDefinitions: {},
    nodeKind: 'PluginTask',
    collectionIteratorItemKind: null,
    ...overrides,
  };
}

function createNode(): NodeModel {
  return new NodeModel(
    1,
    1,
    0,
    0,
    10,
    '1.0.0',
    'Lookup',
    100,
    80,
    320,
    220,
    'Input',
    ['Success'],
    false,
    null,
    null,
    { town: 'String' },
    { Success: { town: 'String', score: 'Integer' } },
  );
}

describe('CanvasNodeLayoutFactory', () => {
  it('builds collapsed, expanded, and partial task-card layouts', () => {
    const factory = new CanvasNodeLayoutFactory();
    const request = layoutRequest();

    const collapsed = factory.buildLayout(request, false);
    const expanded = factory.buildLayout(request, true);
    const partial = factory.buildLayout(request, new Set(['right:Success']));

    expect(collapsed.inputSection?.propertyRows).toEqual([]);
    expect(collapsed.outputSections[0]?.propertyRows).toEqual([]);
    expect(expanded.inputSection?.propertyRows.map((row) => row.name)).toEqual(['town']);
    expect(expanded.outputSections[0]?.propertyRows.map((row) => row.name)).toEqual(['score', 'town']);
    expect(partial.inputSection?.propertyRows).toEqual([]);
    expect(partial.outputSections[0]?.propertyRows.map((row) => row.name)).toEqual(['score', 'town']);
  });

  it('builds compact Collection Iterator layouts without expanded property rows', () => {
    const factory = new CanvasNodeLayoutFactory();
    const layout = factory.buildCollectionIteratorOperatorLayout(layoutRequest({
      width: CANVAS_NODE_LAYOUT_TOKENS.collectionIteratorOperatorWidth,
      inputChannelName: 'Collection',
      outputChannelNames: ['Item'],
      inputProperties: { items: 'Guid[]' },
      outputChannelProperties: { Item: { item: 'Guid' } },
      inputPropertyDefinitions: [],
      nodeKind: 'CollectionIterator',
      collectionIteratorItemKind: 'Guid',
    }));

    expect(layout.totalHeight).toBe(CANVAS_NODE_LAYOUT_TOKENS.collectionIteratorOperatorHeight);
    expect(layout.inputSection?.countLabel).toBe('Guid[]');
    expect(layout.outputSections[0]?.countLabel).toBe('Guid');
    expect(layout.inputSection?.propertyRows).toEqual([]);
    expect(layout.outputSections[0]?.propertyRows).toEqual([]);
    expect(layout.summaryPorts).toHaveLength(2);
  });
});

describe('CanvasNodeLayoutCache', () => {
  it('reuses cached state and section layouts', () => {
    const cache = new CanvasNodeLayoutCache();
    const request = layoutRequest();
    const partialSections = new Set(['right:Success']);

    expect(cache.layoutForState(request, false)).toBe(cache.layoutForState(request, false));
    expect(cache.layoutForState(request, true)).toBe(cache.layoutForState(request, true));
    expect(cache.layoutForExpandedSections(request, partialSections)).toBe(cache.layoutForExpandedSections(request, partialSections));
    expect(cache.layoutForExpandedSections(request, new Set())).toBe(cache.layoutForState(request, false));
    expect(cache.layoutForExpandedSections(request, new Set(['left:Input', 'right:Success']))).toBe(cache.layoutForState(request, true));
  });

  it('treats Data Bridge layouts as always expanded through the cache facade', () => {
    const cache = new CanvasNodeLayoutCache();
    const request = layoutRequest({
      nodeKind: 'DataBridge',
      inputChannelName: 'Bridge',
      inputProperties: { value: 'String' },
      inputPropertyDefinitions: [],
      outputChannelNames: [],
      outputChannelProperties: {},
    });

    expect(cache.layoutForState(request, false)).toBe(cache.layoutForState(request, true));
    expect(cache.layoutForState(request, false).inputSection?.propertyRows.map((row) => row.name)).toEqual(['value']);
  });
});

describe('NodeModel layout facade', () => {
  it('preserves public anchor, port, snap, and clone behavior while delegating layout', () => {
    const node = createNode();
    const expanded = node.withExpanded(true);
    const outputAnchor = expanded.outputAnchors()[0]!;
    const outputPort = expanded.outputPropertyPort(outputAnchor.index, 'score')!;

    expect(expanded.layoutForState(true)).toBe(expanded.layoutForState(true));
    expect(outputAnchor.x).toBe(expanded.x + expanded.width);
    expect(outputPort.x).toBe(expanded.x + expanded.width);
    expect(expanded.portHit(outputPort.x, outputPort.y, 0)?.key).toBe(outputPort.key);
    expect(expanded.snapPointFixedSide(expanded.x + 20, expanded.y - 50, 'left')).toEqual({
      x: expanded.x,
      y: expanded.y,
      side: 'left',
      pos: 0,
    });
    expect(expanded.withInstanceId('node-instance').instanceId).toBe('node-instance');
    expect(node.expanded).toBe(false);
  });
});
