import { describe, expect, it } from 'vitest';
import { CanvasPointerController } from './canvas-pointer-controller';

const pointerController = new CanvasPointerController(4);

describe('CanvasPointerController', () => {
  it('keeps small pointer movement as a pending click', () => {
    expect(pointerController.movedBeyondClickTolerance({ x: 10, y: 10 }, { x: 14, y: 13 })).toBe(false);
    expect(pointerController.pendingClickTransition({
      startCanvas: { x: 10, y: 10 },
      nodeId: 2,
      connectionId: null,
      shift: false,
      toggleSectionKey: null,
    }, { x: 14, y: 13 }, true)).toBe('none');
  });

  it('turns moved editable node clicks into node drags', () => {
    expect(pointerController.pendingClickTransition({
      startCanvas: { x: 10, y: 10 },
      nodeId: 2,
      connectionId: null,
      shift: false,
      toggleSectionKey: null,
    }, { x: 16, y: 10 }, true)).toBe('node-drag');
  });

  it('turns moved empty-canvas clicks into pans', () => {
    expect(pointerController.pendingClickTransition({
      startCanvas: { x: 10, y: 10 },
      nodeId: null,
      connectionId: null,
      shift: false,
      toggleSectionKey: null,
    }, { x: 10, y: 16 }, true)).toBe('pan');
  });

  it('keeps section toggle and connection clicks out of drag transitions', () => {
    expect(pointerController.pendingClickTransition({
      startCanvas: { x: 10, y: 10 },
      nodeId: 2,
      connectionId: null,
      shift: false,
      toggleSectionKey: 'left:Input',
    }, { x: 20, y: 10 }, true)).toBe('none');
    expect(pointerController.pendingClickTransition({
      startCanvas: { x: 10, y: 10 },
      nodeId: null,
      connectionId: 'connection-1',
      shift: false,
      toggleSectionKey: null,
    }, { x: 20, y: 10 }, true)).toBe('none');
  });
});
