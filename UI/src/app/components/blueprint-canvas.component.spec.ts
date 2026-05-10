import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
  signal,
  ɵAfterRenderManager as AfterRenderManager,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
} from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BlueprintCanvasComponent } from './blueprint-canvas.component';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { BlueprintCanvasStateStore } from '../domain/blueprint-canvas-state.store';
import { buildCanvasRenderSnapshot } from '../domain/canvas-render-model';
import { NodeModel } from '../domain/node-model';

const pathfinderOptions = { gridSize: 10, nodeMargin: 20, obstacleBuffer: 2 };

function createSourceNode(
  outputProperties: Record<string, 'String' | 'Guid[]'>,
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

function createIteratorNode(): NodeModel {
  return new NodeModel(
    2,
    0,
    0,
    0,
    0,
    'Native',
    'Collection Iterator',
    420,
    0,
    NodeModel.collectionIteratorOperatorWidth,
    NodeModel.collectionIteratorOperatorHeight,
    'Collection',
    ['Item'],
    false,
    null,
    null,
    { items: 'String[]' },
    { Item: { item: 'String' } },
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

describe('BlueprintCanvasComponent', () => {
  let component: BlueprintCanvasComponent;
  let injector: EnvironmentInjector;
  let store: BlueprintEditorStore;

  beforeEach(() => {
    const providers: Provider[] = [
      BlueprintEditorStore,
      BlueprintCanvasStateStore,
      {
        provide: ChangeDetectionScheduler,
        useValue: {
          notify: vi.fn(),
          runningTick: false,
        },
      },
      {
        provide: EffectScheduler,
        useValue: {
          add: vi.fn(),
          schedule: vi.fn(),
          flush: vi.fn(),
          remove: vi.fn(),
        },
      },
      {
        provide: AfterRenderManager,
        useValue: {
          impl: {
            register: vi.fn(),
            execute: vi.fn(),
            unregister: vi.fn(),
          },
        },
      },
    ];

    injector = createEnvironmentInjector(providers, null as never);
    store = injector.get(BlueprintEditorStore);
    component = runInInjectionContext(injector, () => new BlueprintCanvasComponent());
  });

  afterEach(() => {
    injector.destroy();
  });

  it('defaults to a plain canvas background effect', () => {
    expect(component.backgroundEffect()).toBe('plain');
  });

  it('uses normal connection presentation by default', () => {
    expect(component.connectionPresentation()).toBe('normal');
  });

  it('does not draw the normal connection drag preview while staging phase bridge sources', () => {
    Object.defineProperty(component as any, 'mode', {
      value: () => 'bridge-source',
      configurable: true,
    });

    expect((component as any).shouldDrawConnectionDragPreview()).toBe(false);

    Object.defineProperty(component as any, 'mode', {
      value: () => 'edit',
      configurable: true,
    });

    expect((component as any).shouldDrawConnectionDragPreview()).toBe(true);
  });

  it('clears the backdrop hover point when the pointer leaves', () => {
    (component as any).backdropHoverCanvasPoint.set({ x: 120, y: 88 });

    (component as any).onPointerLeave();

    expect((component as any).backdropHoverCanvasPoint()).toBeNull();
  });

  it('pans the canvas with wheel scrolling by default', () => {
    const viewportOffset = signal({ x: 12, y: 24 });
    const zoomLevel = signal(1);
    (component as any).canvasWrapper = {
      viewportOffset,
      zoomLevel,
    };

    const preventDefault = vi.fn();

    (component as any).onWheel({
      deltaX: 18,
      deltaY: 42,
      deltaMode: 0,
      ctrlKey: false,
      preventDefault,
    } as WheelEvent);

    expect(preventDefault).toHaveBeenCalled();
    expect(viewportOffset()).toEqual({ x: -6, y: -18 });
    expect(zoomLevel()).toBe(1);
  });

  it('zooms the canvas on pinch gestures', () => {
    const viewportOffset = signal({ x: 12, y: 24 });
    const zoomLevel = signal(1);
    (component as any).canvasRef = () => ({
      nativeElement: {
        clientWidth: 800,
        clientHeight: 600,
        getBoundingClientRect: () => ({
          left: 0,
          top: 0,
        }),
      },
    });
    (component as any).canvasWrapper = {
      viewportOffset,
      zoomLevel,
    };

    (component as any).onWheel({
      deltaX: 0,
      deltaY: -24,
      deltaMode: 0,
      ctrlKey: true,
      clientX: 200,
      clientY: 150,
      preventDefault: vi.fn(),
    } as WheelEvent);

    expect(zoomLevel()).toBe(1.05);
    expect(viewportOffset().x).toBeCloseTo(2.6);
    expect(viewportOffset().y).toBeCloseTo(17.7);
  });

  it('zooms the canvas when space is held during wheel scrolling', () => {
    const viewportOffset = signal({ x: 12, y: 24 });
    const zoomLevel = signal(1);
    (component as any).canvasRef = () => ({
      nativeElement: {
        clientWidth: 800,
        clientHeight: 600,
        getBoundingClientRect: () => ({
          left: 0,
          top: 0,
        }),
      },
    });
    (component as any).canvasWrapper = {
      viewportOffset,
      zoomLevel,
    };
    store.setModifierState({ shift: false, ctrlOrMeta: false, space: true });

    (component as any).onWheel({
      deltaX: 0,
      deltaY: 24,
      deltaMode: 0,
      ctrlKey: false,
      clientX: 400,
      clientY: 300,
      preventDefault: vi.fn(),
    } as WheelEvent);

    expect(zoomLevel()).toBe(0.95);
    expect(viewportOffset()).toEqual({ x: 31.400000000000034, y: 37.80000000000001 });
  });

  it('zooms around the visible canvas center when toolbar zoom is used', () => {
    const viewportOffset = signal({ x: 20, y: 40 });
    const zoomLevel = signal(1);
    (component as any).canvasRef = () => ({
      nativeElement: {
        clientWidth: 800,
        clientHeight: 600,
      },
    });
    (component as any).canvasWrapper = {
      viewportOffset,
      zoomLevel,
    };

    component.zoomIn();

    expect(zoomLevel()).toBe(1.1);
    expect(viewportOffset()).toEqual({ x: -18.000000000000057, y: 14 });
  });

  it('blocks the source node as a connection target while dragging', () => {
    const blockedTargetNodeIds = (component as any).getBlockedTargetNodeIds(
      {
        getConnectedNodeIds: () => new Set([2, 3]),
        nodeById: new Map(),
        nodes: [],
      },
      { pointerId: 1, from: 1, anchor: 0, x: 0, y: 0 },
    );

    expect([...blockedTargetNodeIds]).toEqual([1, 2, 3]);
  });

  it('only treats Collection Iterator as eligible for collection output drags', () => {
    const source = createSourceNode({ town: 'String', ids: 'Guid[]' });
    const iterator = createIteratorNode();
    const snapshot = buildCanvasRenderSnapshot([source, iterator], [], new Map(), 'full', pathfinderOptions, false);

    const scalarBlockedTargetNodeIds = (component as any).getBlockedTargetNodeIds(
      snapshot,
      {
        pointerId: 1,
        from: source.id,
        anchor: source.outputAnchors()[0]!.index,
        x: 0,
        y: 0,
        sourceProperty: 'town',
      },
    );
    const collectionBlockedTargetNodeIds = (component as any).getBlockedTargetNodeIds(
      snapshot,
      {
        pointerId: 1,
        from: source.id,
        anchor: source.outputAnchors()[0]!.index,
        x: 0,
        y: 0,
        sourceProperty: 'ids',
      },
    );
    const previewLabels = (component as any).getCollectionIteratorPreviewLabels(
      snapshot,
      {
        pointerId: 1,
        from: source.id,
        anchor: source.outputAnchors()[0]!.index,
        x: 0,
        y: 0,
        sourceProperty: 'ids',
      },
    );

    expect(scalarBlockedTargetNodeIds.has(iterator.id)).toBe(true);
    expect(collectionBlockedTargetNodeIds.has(iterator.id)).toBe(false);
    expect(previewLabels.get(iterator.id)).toBe('Guid[] -> Guid');
  });

  it('tracks hovered connections when the pointer is not over a node', () => {
    (component as any).getNodeByCoordinates = vi.fn(() => undefined);
    (component as any).getConnectionRouteByCoordinates = vi.fn(() => ({
      connection: { id: 'connection-1' },
      routeIndex: 2,
      distance: 0,
    }));

    (component as any).updateHoverState({ x: 140, y: 220 });

    expect((component as any).hoveredConnectionId()).toBe('connection-1');
    expect((component as any).hoveredConnectionRoute()).toEqual({ connectionId: 'connection-1', routeIndex: 2 });
  });

  it('emits node settings requests for clean node clicks', () => {
    const emittedNodeIds: number[] = [];
    const subscription = component.nodeSettingsRequested.subscribe((nodeId) => emittedNodeIds.push(nodeId));

    (component as any).applyClickSelection({
      pointerId: 1,
      startCanvas: { x: 0, y: 0 },
      startWorld: { x: 0, y: 0 },
      nodeId: 12,
      connectionId: null,
      connectionBadgeId: null,
      shift: false,
      toggleSectionKey: null,
    });

    expect(emittedNodeIds).toEqual([12]);
    expect(store.selectedNodeIds()).toEqual([12]);
    subscription.unsubscribe();
  });

  it('does not emit node settings requests when a drag completes', () => {
    const emittedNodeIds: number[] = [];
    const subscription = component.nodeSettingsRequested.subscribe((nodeId) => emittedNodeIds.push(nodeId));

    (component as any).dragNode.set({
      pointerId: 7,
      startWorld: { x: 20, y: 30 },
      nodeIds: [12],
      initialPositions: new Map([[12, { x: 120, y: 160 }]]),
    });
    (component as any).getCoordinates = vi.fn(() => ({
      canvas: { x: 160, y: 180 },
      world: { x: 180, y: 210 },
    }));
    (component as any).updateHoverState = vi.fn();

    (component as any).completePointerInteraction({ pointerId: 7 } as PointerEvent, false);

    expect(emittedNodeIds).toEqual([]);
    subscription.unsubscribe();
  });

  it('chooses the nearest visual connection lane under the pointer', () => {
    const zoomLevel = signal(1);
    const farConnection = {
      id: 'far',
      nearestVisualRoute: vi.fn(() => ({ routeIndex: 0, distance: 4 })),
    };
    const nearConnection = {
      id: 'near',
      nearestVisualRoute: vi.fn(() => ({ routeIndex: 1, distance: 1 })),
    };
    (component as any).canvasWrapper = {
      zoomLevel,
    };
    Object.defineProperty(component as any, 'renderSnapshot', {
      value: () => ({
        queryConnectionsAtPoint: () => [farConnection, nearConnection],
      }),
    });

    const hit = (component as any).getConnectionRouteByCoordinates({ x: 20, y: 30 });

    expect(hit.connection.id).toBe('near');
    expect(hit.routeIndex).toBe(1);
    expect((component as any).getConnectionByCoordinates({ x: 20, y: 30 }).id).toBe('near');
  });

  it('centers on validation focus bounds without changing zoom', () => {
    const viewportOffset = signal({ x: 0, y: 0 });
    const zoomLevel = signal(1.4);
    (component as any).canvasRef = () => ({
      nativeElement: {
        clientWidth: 800,
        clientHeight: 600,
        focus: vi.fn(),
      },
    });
    (component as any).canvasWrapper = {
      viewportOffset,
      zoomLevel,
    };

    component.centerOnBounds({ left: 100, top: 80, right: 300, bottom: 180 });

    expect(zoomLevel()).toBe(1.4);
    expect(viewportOffset()).toEqual({ x: 120, y: 118 });
  });

  it('centers focused bounds inside configured viewport insets', () => {
    const viewportOffset = signal({ x: 0, y: 0 });
    const zoomLevel = signal(1);
    (component as any).canvasRef = () => ({
      nativeElement: {
        clientWidth: 800,
        clientHeight: 600,
      },
    });
    (component as any).canvasWrapper = {
      viewportOffset,
      zoomLevel,
    };
    Object.defineProperty(component as any, 'viewportInsets', {
      value: () => ({ top: 20, right: 200, bottom: 40, left: 40 }),
      configurable: true,
    });

    component.centerOnBounds({ left: 100, top: 80, right: 300, bottom: 180 });

    expect(viewportOffset()).toEqual({ x: 120, y: 160 });
  });

  it('expands the targeted validation property section without toggling it closed', () => {
    const canvasStateStore = injector.get(BlueprintCanvasStateStore);
    const renderedNode = {
      id: 7,
      inputChannelName: 'Input',
      outputChannelNames: [],
    };
    Object.defineProperty(component as any, 'renderSnapshot', {
      value: () => ({
        nodeById: new Map([[7, renderedNode]]),
      }),
    });

    (component as any).ensureValidationFocusSectionExpanded({
      issueId: 'issue-1',
      severity: 'error',
      nodeId: 7,
      channelName: 'Input',
      propertyName: 'town',
      side: 'left',
      pulseKey: 1,
    });
    (component as any).ensureValidationFocusSectionExpanded({
      issueId: 'issue-1',
      severity: 'error',
      nodeId: 7,
      channelName: 'Input',
      propertyName: 'town',
      side: 'left',
      pulseKey: 2,
    });

    expect(canvasStateStore.expandedNodeSections()).toEqual({
      7: ['left:Input'],
    });
  });

  it('resolves validation focus to an exact input property row and port', () => {
    const propertyPort = {
      key: 'left:Input:town',
      x: 96,
      y: 146,
      radius: 5,
      hitBounds: { x: 84, y: 134, width: 24, height: 24 },
    };
    const summaryPort = {
      key: 'left:Input:summary',
      x: 92,
      y: 116,
      radius: 7,
      hitBounds: { x: 78, y: 100, width: 28, height: 32 },
    };
    const renderedNode = {
      id: 7,
      x: 100,
      y: 80,
      width: 320,
      height: 180,
      inputChannelName: 'Input',
      outputChannelNames: [],
      layout: {
        inputSection: {
          key: 'left:Input',
          headerTop: 100,
          bottom: 164,
          summaryPort,
          propertyRows: [
            {
              name: 'town',
              top: 132,
              height: 30,
              port: propertyPort,
            },
          ],
        },
        outputSections: [],
      },
    };
    const snapshot = {
      nodeById: new Map([[7, renderedNode]]),
      connectionById: new Map(),
    };

    const resolved = (component as any).resolveValidationFocus(snapshot, {
      issueId: 'issue-1',
      severity: 'error',
      nodeId: 7,
      channelName: 'Input',
      propertyName: 'town',
      side: 'left',
      pulseKey: 1,
    });

    expect(resolved.sectionKey).toBe('left:Input');
    expect([...resolved.portKeys]).toEqual(['7:left:Input:town']);
    expect(resolved.bounds.left).toBe(78);
    expect(resolved.bounds.right).toBe(406);
  });

  it('resolves validation focus to a specific connection route', () => {
    const renderedConnection = {
      id: 'connection-1',
      from: { id: 1 },
      to: { id: 2 },
      anchor: 0,
      routeBounds: { left: 80, top: 100, right: 420, bottom: 180 },
      routes: [
        {
          bounds: { left: 90, top: 110, right: 410, bottom: 170 },
          startPortKey: 'right:Success:town',
          endPortKey: 'left:Input:town',
        },
      ],
    };
    const snapshot = {
      nodeById: new Map(),
      connectionById: new Map([['connection-1', renderedConnection]]),
    };

    const resolved = (component as any).resolveValidationFocus(snapshot, {
      issueId: 'issue-1',
      severity: 'warning',
      connectionId: 'connection-1',
      routeIndex: 0,
      pulseKey: 1,
    });

    expect(resolved.connectionId).toBe('connection-1');
    expect(resolved.routeIndex).toBe(0);
    expect([...resolved.portKeys]).toEqual([
      '1:right:Success:town',
      '2:left:Input:town',
    ]);
    expect(resolved.bounds).toEqual({ left: 90, top: 110, right: 410, bottom: 170 });
  });
});
