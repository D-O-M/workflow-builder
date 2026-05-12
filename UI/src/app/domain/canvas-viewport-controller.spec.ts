import { signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { CanvasViewportController } from './canvas-viewport-controller';

function canvasStub(options: {
  readonly width: number;
  readonly height: number;
  readonly left?: number;
  readonly top?: number;
}): HTMLCanvasElement {
  return {
    clientWidth: options.width,
    clientHeight: options.height,
    getBoundingClientRect: () => ({
      left: options.left ?? 0,
      top: options.top ?? 0,
      right: (options.left ?? 0) + options.width,
      bottom: (options.top ?? 0) + options.height,
      width: options.width,
      height: options.height,
    }),
  } as unknown as HTMLCanvasElement;
}

describe('CanvasViewportController', () => {
  it('converts between client and world coordinates through the viewport state', () => {
    const viewportOffset = signal({ x: 20, y: 30 });
    const zoomLevel = signal(2);
    const controller = new CanvasViewportController(
      () => canvasStub({ width: 800, height: 600, left: 100, top: 50 }),
      () => ({ viewportOffset, zoomLevel }),
    );

    expect(controller.clientToWorldPoint(140, 110)).toEqual({ x: 10, y: 15 });
    expect(controller.worldToClientPoint({ x: 10, y: 15 })).toEqual({ clientX: 140, clientY: 110 });
  });

  it('zooms around the supplied focal canvas point', () => {
    const viewportOffset = signal({ x: 20, y: 30 });
    const zoomLevel = signal(1);
    const changed = vi.fn();
    const controller = new CanvasViewportController(
      () => canvasStub({ width: 800, height: 600 }),
      () => ({ viewportOffset, zoomLevel }),
      undefined,
      changed,
    );

    expect(controller.adjustZoom(0.5, { x: 200, y: 100 })).toBe(true);

    expect(zoomLevel()).toBe(1.5);
    expect(viewportOffset()).toEqual({ x: -70, y: -5 });
    expect(changed).toHaveBeenCalledOnce();
  });

  it('centers world bounds within configured viewport insets', () => {
    const viewportOffset = signal({ x: 0, y: 0 });
    const zoomLevel = signal(1);
    const controller = new CanvasViewportController(
      () => canvasStub({ width: 800, height: 600 }),
      () => ({ viewportOffset, zoomLevel }),
      () => ({ top: 40, right: 80, bottom: 0, left: 120 }),
    );

    controller.centerOnBounds({ left: 100, top: 50, right: 300, bottom: 150 });

    expect(viewportOffset()).toEqual({ x: 220, y: 220 });
  });
});
