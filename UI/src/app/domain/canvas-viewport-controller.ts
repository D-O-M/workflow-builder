import { WritableSignal } from '@angular/core';
import { CanvasBounds, CanvasRenderSnapshot } from './canvas-render-model';
import { EMPTY_CANVAS_VIEWPORT_INSETS, CanvasViewportInsets, CanvasViewportState } from './canvas-runtime';
import { Point } from './node-model';

interface CanvasViewportAdapter {
  readonly viewportOffset: WritableSignal<Point>;
  readonly zoomLevel: WritableSignal<number>;
}

export class CanvasViewportController {
  constructor(
    private readonly canvas: () => HTMLCanvasElement | null,
    private readonly viewport: () => CanvasViewportAdapter | null,
    private readonly viewportInsets: () => CanvasViewportInsets = () => EMPTY_CANVAS_VIEWPORT_INSETS,
    private readonly viewportChanged: () => void = () => undefined,
  ) {}

  state(): CanvasViewportState | null {
    const canvas = this.canvas();
    const viewport = this.viewport();
    if (!canvas || !viewport) {
      return null;
    }

    return {
      offset: viewport.viewportOffset(),
      zoom: viewport.zoomLevel(),
      viewportSize: {
        width: canvas.clientWidth,
        height: canvas.clientHeight,
      },
      insets: this.normalizedViewportInsets(canvas),
    };
  }

  syncBackingSize(context: CanvasRenderingContext2D | null | undefined): void {
    const canvas = this.canvas();
    if (!canvas) {
      return;
    }

    const dpr = window.devicePixelRatio || 1;
    const cssWidth = Math.max(1, Math.floor(canvas.clientWidth));
    const cssHeight = Math.max(1, Math.floor(canvas.clientHeight));
    const width = Math.max(1, Math.floor(cssWidth * dpr));
    const height = Math.max(1, Math.floor(cssHeight * dpr));

    if (canvas.width !== width) {
      canvas.width = width;
    }

    if (canvas.height !== height) {
      canvas.height = height;
    }

    if (context) {
      context.setTransform(1, 0, 0, 1, 0, 0);
      context.scale(dpr, dpr);
    }
  }

  resetView(snapshot: CanvasRenderSnapshot): void {
    const viewport = this.viewport();
    const canvas = this.canvas();
    if (!viewport || !canvas) {
      return;
    }

    const bounds = this.graphContentBounds(snapshot);
    if (!bounds) {
      viewport.zoomLevel.set(1);
      viewport.viewportOffset.set({ x: 0, y: 0 });
      this.viewportChanged();
      return;
    }

    const zoom = this.zoomLevelForBounds(bounds, canvas);
    const focus = this.viewportFocusCanvasPoint(canvas);
    const centerX = (bounds.left + bounds.right) / 2;
    const centerY = (bounds.top + bounds.bottom) / 2;
    viewport.zoomLevel.set(zoom);
    viewport.viewportOffset.set({
      x: focus.x - centerX * zoom,
      y: focus.y - centerY * zoom,
    });
    this.viewportChanged();
  }

  centerOnBounds(bounds: CanvasBounds): void {
    const viewport = this.viewport();
    const canvas = this.canvas();
    if (!viewport || !canvas) {
      return;
    }

    const zoom = viewport.zoomLevel();
    const focus = this.viewportFocusCanvasPoint(canvas);
    const centerX = (bounds.left + bounds.right) / 2;
    const centerY = (bounds.top + bounds.bottom) / 2;
    viewport.viewportOffset.set({
      x: focus.x - centerX * zoom,
      y: focus.y - centerY * zoom,
    });
    this.viewportChanged();
  }

  centerOnWorldPoint(point: Point): void {
    const viewport = this.viewport();
    const canvas = this.canvas();
    if (!viewport || !canvas) {
      return;
    }

    const zoom = viewport.zoomLevel();
    const focus = this.viewportFocusCanvasPoint(canvas);
    viewport.viewportOffset.set({
      x: focus.x - point.x * zoom,
      y: focus.y - point.y * zoom,
    });
    this.viewportChanged();
  }

  zoomIn(focalCanvasPoint?: Point): void {
    this.adjustZoom(0.1, focalCanvasPoint);
  }

  zoomOut(focalCanvasPoint?: Point): void {
    this.adjustZoom(-0.1, focalCanvasPoint);
  }

  adjustZoom(delta: number, focalCanvasPoint?: Point): boolean {
    const viewport = this.viewport();
    const canvas = this.canvas();
    if (!viewport) {
      return false;
    }

    const currentZoom = viewport.zoomLevel();
    const nextZoom = Math.min(3, Math.max(0.2, currentZoom + delta));
    if (Number(nextZoom.toFixed(2)) === currentZoom) {
      return false;
    }

    const offset = viewport.viewportOffset();
    const focus = focalCanvasPoint ?? (
      canvas
        ? this.viewportFocusCanvasPoint(canvas)
        : { x: 0, y: 0 }
    );
    const worldFocus = {
      x: (focus.x - offset.x) / currentZoom,
      y: (focus.y - offset.y) / currentZoom,
    };

    viewport.zoomLevel.set(Number(nextZoom.toFixed(2)));
    viewport.viewportOffset.set({
      x: focus.x - worldFocus.x * nextZoom,
      y: focus.y - worldFocus.y * nextZoom,
    });
    this.viewportChanged();
    return true;
  }

  panBy(delta: Point): void {
    const viewport = this.viewport();
    if (!viewport) {
      return;
    }

    viewport.viewportOffset.update((currentOffset) => ({
      x: currentOffset.x - delta.x,
      y: currentOffset.y - delta.y,
    }));
    this.viewportChanged();
  }

  focusCanvas(): void {
    this.canvas()?.focus();
  }

  containsClientPoint(point: { readonly clientX: number; readonly clientY: number }, tolerance = 0): boolean {
    const canvas = this.canvas();
    if (!canvas) {
      return false;
    }

    const rect = canvas.getBoundingClientRect();
    return point.clientX >= rect.left - tolerance &&
      point.clientX <= rect.right + tolerance &&
      point.clientY >= rect.top - tolerance &&
      point.clientY <= rect.bottom + tolerance;
  }

  clientToWorldPoint(clientX: number, clientY: number): Point | null {
    const viewport = this.viewport();
    const canvas = this.canvas();
    if (!viewport || !canvas) {
      return null;
    }

    const rect = canvas.getBoundingClientRect();
    const offset = viewport.viewportOffset();
    const zoom = viewport.zoomLevel();
    return {
      x: (clientX - rect.left - offset.x) / zoom,
      y: (clientY - rect.top - offset.y) / zoom,
    };
  }

  worldToClientPoint(point: Point): { clientX: number; clientY: number } | null {
    const viewport = this.viewport();
    const canvas = this.canvas();
    if (!viewport || !canvas) {
      return null;
    }

    const rect = canvas.getBoundingClientRect();
    const offset = viewport.viewportOffset();
    const zoom = viewport.zoomLevel();
    return {
      clientX: rect.left + point.x * zoom + offset.x,
      clientY: rect.top + point.y * zoom + offset.y,
    };
  }

  coordinatesFromEvent(event: MouseEvent | PointerEvent): { canvas: Point; world: Point } {
    const canvas = this.canvas();
    const viewport = this.viewport();
    if (!canvas || !viewport) {
      return {
        canvas: { x: 0, y: 0 },
        world: { x: 0, y: 0 },
      };
    }

    const rect = canvas.getBoundingClientRect();
    const canvasPoint = {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
    };
    const offset = viewport.viewportOffset();
    const zoom = viewport.zoomLevel();
    return {
      canvas: canvasPoint,
      world: {
        x: (canvasPoint.x - offset.x) / zoom,
        y: (canvasPoint.y - offset.y) / zoom,
      },
    };
  }

  private graphContentBounds(snapshot: CanvasRenderSnapshot): CanvasBounds | null {
    if (snapshot.nodes.length === 0) {
      return null;
    }

    const padding = 80;
    return snapshot.nodes.reduce<CanvasBounds | null>((bounds, node) => {
      const nodeBounds = node.interactionBounds();
      const paddedBounds = {
        left: nodeBounds.left - padding,
        top: nodeBounds.top - padding,
        right: nodeBounds.right + padding,
        bottom: nodeBounds.bottom + padding,
      };
      if (!bounds) {
        return paddedBounds;
      }

      return {
        left: Math.min(bounds.left, paddedBounds.left),
        top: Math.min(bounds.top, paddedBounds.top),
        right: Math.max(bounds.right, paddedBounds.right),
        bottom: Math.max(bounds.bottom, paddedBounds.bottom),
      };
    }, null);
  }

  private zoomLevelForBounds(bounds: CanvasBounds, canvas: HTMLCanvasElement): number {
    const insets = this.normalizedViewportInsets(canvas);
    const availableWidth = Math.max(240, canvas.clientWidth - insets.left - insets.right - 56);
    const availableHeight = Math.max(180, canvas.clientHeight - insets.top - insets.bottom - 56);
    const boundsWidth = Math.max(1, bounds.right - bounds.left);
    const boundsHeight = Math.max(1, bounds.bottom - bounds.top);
    const fitZoom = Math.min(1, availableWidth / boundsWidth, availableHeight / boundsHeight);
    return Number(Math.max(0.35, Math.min(1, fitZoom)).toFixed(2));
  }

  private viewportFocusCanvasPoint(canvas: HTMLCanvasElement): Point {
    const insets = this.normalizedViewportInsets(canvas);
    const availableWidth = Math.max(1, canvas.clientWidth - insets.left - insets.right);
    const availableHeight = Math.max(1, canvas.clientHeight - insets.top - insets.bottom);
    return {
      x: insets.left + availableWidth / 2,
      y: insets.top + availableHeight / 2,
    };
  }

  private normalizedViewportInsets(canvas: HTMLCanvasElement): CanvasViewportInsets {
    const rawInsets = this.viewportInsets();
    const left = Math.max(0, rawInsets.left);
    const right = Math.max(0, rawInsets.right);
    const top = Math.max(0, rawInsets.top);
    const bottom = Math.max(0, rawInsets.bottom);
    const horizontalBudget = Math.max(0, canvas.clientWidth - 1);
    const verticalBudget = Math.max(0, canvas.clientHeight - 1);
    const horizontalScale = left + right > horizontalBudget && left + right > 0
      ? horizontalBudget / (left + right)
      : 1;
    const verticalScale = top + bottom > verticalBudget && top + bottom > 0
      ? verticalBudget / (top + bottom)
      : 1;

    return {
      left: left * horizontalScale,
      right: right * horizontalScale,
      top: top * verticalScale,
      bottom: bottom * verticalScale,
    };
  }
}
