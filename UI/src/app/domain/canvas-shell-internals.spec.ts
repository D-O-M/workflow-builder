import { describe, expect, it, vi } from 'vitest';
import { ConnectionModel } from './connection-model';
import { CanvasHitTester } from './canvas-hit-tester';
import { CanvasInteractionController } from './canvas-interaction-controller';
import { CanvasRenderFrameFactory } from './canvas-render-frame-factory';
import { buildCanvasRenderSnapshot, CanvasRenderSnapshot, RenderedConnection } from './canvas-render-model';
import { canvasModeCapabilities } from './canvas-mode-policy';
import { CanvasValidationFocusResolver } from './canvas-validation-focus-resolver';
import { NodeModel, TaskPropertyType } from './node-model';

const pathfinderOptions = { gridSize: 10, nodeMargin: 20, obstacleBuffer: 2 };

function createSourceNode(
  outputProperties: Record<string, TaskPropertyType>,
): NodeModel {
  return new NodeModel(
    1,
    1,
    0,
    0,
    10,
    '1.0.0',
    'Source',
    0,
    0,
    320,
    220,
    null,
    ['Success'],
    false,
    null,
    null,
    {},
    { Success: outputProperties },
  );
}

function createTargetNode(): NodeModel {
  return new NodeModel(
    2,
    2,
    0,
    0,
    20,
    '1.0.0',
    'Target',
    420,
    0,
    320,
    220,
    'Input',
    [],
    false,
    null,
    null,
    { town: 'String' },
    {},
  );
}

function createIteratorNode(): NodeModel {
  return new NodeModel(
    3,
    0,
    0,
    0,
    0,
    'Native',
    'Collection Iterator',
    420,
    260,
    NodeModel.collectionIteratorOperatorWidth,
    NodeModel.collectionIteratorOperatorHeight,
    'Collection',
    ['Item'],
    false,
    null,
    null,
    { items: 'Guid[]' },
    { Item: { item: 'Guid' } },
    {},
    [],
    'Collection',
    ['Item'],
    {},
    false,
    'CollectionIterator',
    null,
  );
}

function expandedSnapshot(nodes: readonly NodeModel[], connections: readonly ConnectionModel[] = []): CanvasRenderSnapshot {
  const expandedSections = new Map(nodes.map((node) => [
    node.id,
    new Set([
      ...(node.inputChannelName ? [`left:${node.inputChannelName}`] : []),
      ...node.outputChannelNames.map((channelName) => `right:${channelName}`),
    ]),
  ] as const));

  return buildCanvasRenderSnapshot(nodes, connections, expandedSections, 'full', pathfinderOptions, false);
}

describe('CanvasHitTester', () => {
  it('chooses the nearest visual connection route under the pointer', () => {
    const tester = new CanvasHitTester();
    const farConnection = {
      id: 'far',
      nearestVisualRoute: vi.fn(() => ({ routeIndex: 0, distance: 4 })),
    } as unknown as RenderedConnection;
    const nearConnection = {
      id: 'near',
      nearestVisualRoute: vi.fn(() => ({ routeIndex: 1, distance: 1 })),
    } as unknown as RenderedConnection;
    const snapshot = {
      queryConnectionsAtPoint: () => [farConnection, nearConnection],
    } as unknown as CanvasRenderSnapshot;

    const hit = tester.connectionRouteAt({ snapshot, zoomLevel: 1 }, { x: 20, y: 30 });

    expect(hit?.connection.id).toBe('near');
    expect(hit?.routeIndex).toBe(1);
  });

  it('blocks the source, connected targets, and incompatible collection iterators', () => {
    const tester = new CanvasHitTester();
    const source = createSourceNode({ town: 'String', ids: 'Guid[]' });
    const iterator = createIteratorNode();
    const snapshot = expandedSnapshot([source, iterator]);
    const sourceAnchor = source.outputAnchors()[0]!.index;

    const scalarBlocked = tester.blockedTargetNodeIds(snapshot, {
      from: source.id,
      anchor: sourceAnchor,
      sourceProperty: 'town',
    });
    const collectionBlocked = tester.blockedTargetNodeIds(snapshot, {
      from: source.id,
      anchor: sourceAnchor,
      sourceProperty: 'ids',
    });
    const previewLabels = tester.collectionIteratorPreviewLabels(snapshot, {
      from: source.id,
      anchor: sourceAnchor,
      sourceProperty: 'ids',
    });

    expect(scalarBlocked.has(iterator.id)).toBe(true);
    expect(collectionBlocked.has(iterator.id)).toBe(false);
    expect(previewLabels.get(iterator.id)).toBe('Guid[] -> Guid');
  });
});

describe('CanvasValidationFocusResolver', () => {
  it('groups node issue summaries by severity and property', () => {
    const resolver = new CanvasValidationFocusResolver();

    const summaries = resolver.buildNodeIssueSummaries([
      {
        issueId: 'issue-1',
        severity: 'warning',
        nodeId: 7,
        message: 'Missing optional mapping',
        channelName: 'Input',
        propertyName: 'town',
        side: 'left',
      },
      {
        issueId: 'issue-2',
        severity: 'error',
        nodeId: 7,
        message: 'Missing required mapping',
        channelName: 'Input',
        propertyName: 'town',
        side: 'left',
      },
    ]);

    const summary = summaries.get(7);
    expect(summary?.severity).toBe('error');
    expect(summary?.errorCount).toBe(1);
    expect(summary?.warningCount).toBe(1);
    expect(summary?.propertyIssues.get('left:Input:town')).toEqual({ severity: 'error', count: 2 });
  });

  it('resolves validation focus to an exact input property row and port', () => {
    const resolver = new CanvasValidationFocusResolver();
    const source = createSourceNode({ forecast: 'String' });
    const target = createTargetNode();
    const snapshot = expandedSnapshot([source, target]);

    const resolved = resolver.resolveValidationFocus({
      snapshot,
      focus: {
        issueId: 'issue-1',
        severity: 'error',
        nodeId: target.id,
        channelName: 'Input',
        propertyName: 'town',
        side: 'left',
        pulseKey: 1,
      },
    });

    expect(resolved?.sectionKey).toBe('left:Input');
    expect([...resolved?.portKeys ?? []]).toEqual(['2:left:Input:town']);
    expect(resolved?.bounds?.left).toBeLessThanOrEqual(target.x);
    expect(resolved?.bounds?.right).toBeGreaterThan(target.x + target.width - 20);
  });
});

describe('CanvasInteractionController', () => {
  it('moves pending clicks into node drag, pan, or no-op transitions', () => {
    const controller = new CanvasInteractionController(4);
    const pending = {
      pointerId: 1,
      startCanvas: { x: 10, y: 10 },
      startWorld: { x: 20, y: 20 },
      nodeId: 12,
      connectionId: null,
      connectionBadgeId: null,
      shift: false,
      toggleSectionKey: null,
    };

    expect(controller.pendingClickTransition(pending, { x: 12, y: 12 }, true)).toBe('none');
    expect(controller.pendingClickTransition(pending, { x: 20, y: 10 }, true)).toBe('node-drag');
    expect(controller.pendingClickTransition({ ...pending, nodeId: null }, { x: 20, y: 10 }, true)).toBe('pan');
  });

  it('turns clean clicks into explicit selection and output effects', () => {
    const controller = new CanvasInteractionController(4);
    const effects = controller.clickEffects({
      pointerId: 1,
      startCanvas: { x: 0, y: 0 },
      startWorld: { x: 0, y: 0 },
      nodeId: 12,
      connectionId: null,
      connectionBadgeId: null,
      shift: false,
      toggleSectionKey: null,
    }, {
      readOnly: false,
      canEditGraph: true,
      nodeIsDataBridge: () => false,
    });

    expect(effects).toEqual([
      { kind: 'selectOnlyNode', nodeId: 12 },
      { kind: 'emitNodeSettingsRequested', nodeId: 12 },
    ]);
  });

  it('clears hover and pointer lifecycle state independently', () => {
    const controller = new CanvasInteractionController(4);
    controller.setHover({
      nodeId: 1,
      nodeIssueNodeId: 1,
      portKey: 'right:Success:forecast',
      sectionKey: null,
      connectionId: null,
      connectionRoute: null,
    });
    controller.dragConnection.set({ pointerId: 1, from: 1, anchor: 0, x: 10, y: 10 });

    controller.clearHover();
    expect(controller.hoveredNodeId()).toBeNull();
    expect(controller.activePointerLifecycle()).toBe(true);

    controller.clearPointerLifecycle();
    expect(controller.activePointerLifecycle()).toBe(false);
  });
});

describe('CanvasRenderFrameFactory', () => {
  it('keeps bridge-source mode from drawing the normal drag preview', () => {
    const factory = new CanvasRenderFrameFactory();
    const source = createSourceNode({ forecast: 'String' });
    const target = createTargetNode();
    const snapshot = expandedSnapshot([source, target]);

    const frame = factory.createFramePlan({
      snapshot,
      capabilities: canvasModeCapabilities('bridge-source'),
      connectionPresentation: 'ambient',
      interaction: {
        nodeId: null,
        nodeIssueNodeId: null,
        portKey: null,
        sectionKey: null,
        connectionId: null,
        connectionRoute: null,
        backdropHoverCanvasPoint: null,
        pendingClick: null,
        dragNode: null,
        dragConnection: {
          pointerId: 1,
          from: source.id,
          anchor: source.outputAnchors()[0]!.index,
          x: target.x,
          y: target.y,
          sourceProperty: 'forecast',
        },
        pan: null,
        marquee: null,
      },
      readonlySelectedNodeId: null,
      selectedNodeIds: new Set<number>(),
      selectedConnectionIds: new Set<string>(),
      validationFocus: null,
      nodeIssueSummaries: new Map(),
    });

    expect(frame.connectionPreview).toBeNull();
    expect(frame.nodeLayer.options.passiveSummaryPorts).toBe(true);
  });
});
