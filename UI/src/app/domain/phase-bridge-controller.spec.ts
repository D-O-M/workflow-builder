import { describe, expect, it, vi } from 'vitest';
import { CanvasOutputPropertyDragEvent } from '../components/blueprint-canvas-contract';
import { DataBridgeMappingDraft } from './phase-series-canvas';
import { PhaseBridgeController } from './phase-bridge-controller';

class FakePointerTarget {
  private readonly listeners = new Map<string, Set<(event: PointerEvent) => void>>();

  addEventListener(type: 'pointermove' | 'pointerup' | 'pointercancel', listener: (event: PointerEvent) => void): void {
    const listeners = this.listeners.get(type) ?? new Set<(event: PointerEvent) => void>();
    listeners.add(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type: 'pointermove' | 'pointerup' | 'pointercancel', listener: (event: PointerEvent) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  dispatch(type: 'pointermove' | 'pointerup' | 'pointercancel', event: Partial<PointerEvent>): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event as PointerEvent);
    }
  }
}

function sourceDragEvent(overrides: Partial<CanvasOutputPropertyDragEvent> = {}): CanvasOutputPropertyDragEvent {
  return {
    clientX: 100,
    clientY: 120,
    cancelled: false,
    nodeId: 7,
    nodeInstanceId: 'source-node',
    nodeName: 'Source',
    channelName: 'Output',
    propertyName: 'Result',
    propertyKind: 'String',
    ...overrides,
  };
}

function mapping(overrides: Partial<DataBridgeMappingDraft> = {}): DataBridgeMappingDraft {
  return {
    id: 'mapping-1',
    sourceNodeId: 7,
    sourceBlueprintNodeId: 'source-node',
    sourceNodeName: 'Source',
    sourceChannelName: 'Output',
    sourceProperty: 'Result',
    propertyKind: 'String',
    bridgePosition: 0.4,
    targetNodeId: null,
    targetBlueprintNodeId: null,
    targetNodeName: null,
    targetProperty: null,
    ...overrides,
  };
}

describe('PhaseBridgeController', () => {
  it('stages source drags only when released on the bridge', () => {
    const controller = new PhaseBridgeController();
    controller.startSourceDrag(sourceDragEvent(), 0.4);

    expect(controller.drag()).toEqual(expect.objectContaining({
      kind: 'source-output',
      bridgePosition: 0.4,
      sourceProperty: 'Result',
    }));

    const rejected = controller.finishSourceDrag({
      clientX: -10,
      clientY: -10,
      cancelled: false,
      bridgePosition: null,
    });

    expect(rejected.kind).toBe('none');
    expect(controller.drag()).toBeNull();
    expect(controller.previewPosition()).toBeNull();
    expect(controller.dropMessage()).toBe('Drop on the bridge.');

    controller.startSourceDrag(sourceDragEvent(), 0.65);
    const accepted = controller.finishSourceDrag({
      clientX: 160,
      clientY: 120,
      cancelled: false,
      bridgePosition: 0.65,
    });

    expect(accepted).toEqual({
      kind: 'stageSource',
      bridgePosition: 0.65,
      source: expect.objectContaining({
        sourceNodeName: 'Source',
        sourceProperty: 'Result',
        propertyKind: 'String',
      }),
    });
    expect(controller.dropMessage()).toBeNull();
  });

  it('cancels bridge drags and clears preview state', () => {
    const controller = new PhaseBridgeController();
    controller.startSourceDrag(sourceDragEvent(), 0.5);
    controller.dropMessage.set('Drop on the bridge.');

    controller.cancelDrag();

    expect(controller.drag()).toBeNull();
    expect(controller.previewPosition()).toBeNull();
    expect(controller.dropMessage()).toBeNull();
  });

  it('tracks bridge-output drags through a pointer target and reports invalid drops', () => {
    const pointerTarget = new FakePointerTarget();
    const scheduleRefresh = vi.fn();
    const complete = vi.fn();
    const controller = new PhaseBridgeController(scheduleRefresh, pointerTarget);
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    controller.startBridgeOutputDrag({
      clientX: 20,
      clientY: 30,
      preventDefault,
      stopPropagation,
    } as unknown as PointerEvent, mapping(), { complete });
    pointerTarget.dispatch('pointermove', { clientX: 42, clientY: 66 });
    pointerTarget.dispatch('pointerup', { clientX: 50, clientY: 70 });

    expect(preventDefault).toHaveBeenCalled();
    expect(stopPropagation).toHaveBeenCalled();
    expect(scheduleRefresh).toHaveBeenCalled();
    expect(controller.drag()).toEqual(expect.objectContaining({
      kind: 'bridge-output',
      clientX: 42,
      clientY: 66,
    }));
    expect(complete).toHaveBeenCalledWith('mapping-1', 50, 70, false);

    expect(controller.prepareDestinationDrop(false)).toBe(true);
    controller.rejectDestinationDrop();

    expect(controller.drag()).toBeNull();
    expect(controller.dropMessage()).toBe('Drop on a compatible destination input.');
  });

  it('treats a bridge-output click as selection instead of a drag completion', () => {
    const pointerTarget = new FakePointerTarget();
    const complete = vi.fn();
    const controller = new PhaseBridgeController(undefined, pointerTarget);

    controller.startBridgeOutputDrag({
      clientX: 20,
      clientY: 30,
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as PointerEvent, mapping(), { complete });
    pointerTarget.dispatch('pointerup', { clientX: 21, clientY: 32 });

    expect(complete).not.toHaveBeenCalled();
    expect(controller.drag()).toBeNull();
    expect(controller.selection()).toEqual({ mappingId: 'mapping-1', side: 'destination' });
  });

  it('translates selected bridge anchors into delete effects', () => {
    const controller = new PhaseBridgeController();

    controller.select('mapping-1', 'source');
    expect(controller.deleteSelectionEffect()).toEqual({ kind: 'removeMapping', mappingId: 'mapping-1' });
    expect(controller.selection()).toBeNull();

    controller.select('mapping-2', 'destination');
    expect(controller.deleteSelectionEffect()).toEqual({ kind: 'clearTarget', mappingId: 'mapping-2' });
    expect(controller.selection()).toBeNull();
  });
});
