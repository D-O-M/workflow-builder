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

describe('BlueprintCanvasComponent', () => {
  let component: BlueprintCanvasComponent;
  let injector: EnvironmentInjector;
  let store: BlueprintEditorStore;

  beforeEach(() => {
    const providers: Provider[] = [
      BlueprintEditorStore,
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
});
